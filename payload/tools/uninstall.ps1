# ============================================================================
# uninstall.ps1 - 一键清理 / 回退部署
#
# 按「先停、再删引用、最后删文件」的顺序回退，任何一步失败都不影响后续步骤：
#   1. 停止三个服务（只停本套件端口上的进程）
#   2. 删除桌面快捷方式
#   3. 删除 ~\.codebuddy\rules\deploy-target.mdc（只删这份，不碰其它规则）
#   4. 删除部署目录（保留 data\ 需显式 -KeepData）
#
# -FromInside 用于从目标目录内部运行（一键清理.cmd 的场景）：
# 目录删不干净时交给后台进程延迟删除，避免"程序正在使用"。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Target "D:\Kit"
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Target "D:\Kit" -KeepData
# ============================================================================
param(
  [string]$Target = "$env:LOCALAPPDATA\CodeBuddyWorkKit",
  [switch]$KeepData,
  [switch]$FromInside,
  [switch]$Json
)

$ErrorActionPreference = 'Continue'
$Target = [System.IO.Path]::GetFullPath($Target)
$steps = New-Object System.Collections.Generic.List[object]

function Add-Step {
  param([string]$Name, [string]$Status, [string]$Detail)
  $script:steps.Add([pscustomobject]@{ name = $Name; status = $Status; detail = $Detail })
  if (-not $Json) {
    $tag = switch ($Status) { 'done' { '完成' } 'skip' { '跳过' } default { '失败' } }
    $color = switch ($Status) { 'done' { 'Green' } 'skip' { 'DarkYellow' } default { 'Red' } }
    Write-Host ("  [{0}] {1}" -f $tag, $Name) -ForegroundColor $color -NoNewline
    Write-Host ("  {0}" -f $Detail) -ForegroundColor DarkGray
  }
}

if (-not $Json) {
  Write-Host ''
  Write-Host '  回退部署' -ForegroundColor Cyan
  Write-Host '  ============================================' -ForegroundColor DarkGray
}

# ------------------------------------------------------------------ 1. 停服务
$stopJs = Join-Path $Target 'tools\stop.mjs'
$nodeExe = Join-Path $Target 'runtime\node.exe'
$node = if (Test-Path $nodeExe) { $nodeExe } else { 'node' }

if (Test-Path $stopJs) {
  try {
    $out = & $node $stopJs all 2>&1
    Add-Step '停止服务' 'done' (($out | Select-Object -Last 1) -join ' ')
  } catch {
    Add-Step '停止服务' 'fail' $_.Exception.Message
  }
} else {
  # 目录已被部分删除：直接按端口找
  $configFile = Join-Path $Target 'config\deploy.json'
  $ports = @(47910, 47821, 47822)
  if (Test-Path $configFile) {
    try {
      $cfg = Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
      $ports = @([int]$cfg.ports.codevault, [int]$cfg.ports.pulse, [int]$cfg.ports.tuner)
    } catch { }
  }
  $killed = 0
  foreach ($port in $ports) {
    $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    foreach ($item in $conn) {
      try { Stop-Process -Id $item.OwningProcess -Force -ErrorAction Stop; $killed += 1 } catch { }
    }
  }
  Add-Step '停止服务' 'done' ("按端口停止 {0} 个进程" -f $killed)
}

# ------------------------------------------------------------------ 2. 桌面快捷方式
try {
  $desktop = [Environment]::GetFolderPath('Desktop')
  $lnk = Join-Path $desktop 'CodeBuddy 工作套件.lnk'
  if (Test-Path $lnk) {
    Remove-Item $lnk -Force
    Add-Step '桌面快捷方式' 'done' '已删除'
  } else {
    Add-Step '桌面快捷方式' 'skip' '不存在'
  }
} catch {
  Add-Step '桌面快捷方式' 'fail' $_.Exception.Message
}

# ------------------------------------------------------------------ 2b. 注册表登记
$REG_PATH = 'HKCU:\Software\CodeBuddyWorkKit'
try {
  if (Test-Path $REG_PATH) {
    $reg = Get-ItemProperty -Path $REG_PATH -ErrorAction SilentlyContinue
    $recorded = if ($reg -and $reg.InstallPath) { [string]$reg.InstallPath } else { '' }
    # 只有当登记的就是本次要卸载的目录时才删，避免卸载 A 目录把 B 目录的登记抹掉
    if (-not $recorded -or ($recorded -ieq $Target)) {
      Remove-Item -Path $REG_PATH -Recurse -Force
      Add-Step '注册表登记' 'done' '已移除 HKCU\Software\CodeBuddyWorkKit'
    } else {
      Add-Step '注册表登记' 'skip' ("登记的是另一个目录：{0}" -f $recorded)
    }
  } else {
    Add-Step '注册表登记' 'skip' '不存在'
  }
} catch {
  Add-Step '注册表登记' 'fail' $_.Exception.Message
}

# ------------------------------------------------------------------ 3. 规则（多平台）
# 两种写法要分别处理：
#   独占文件 -> 直接删；聚合文件 -> 只删受管区块，用户自己的内容必须保留
$BEGIN_MARK = '<!-- CodeBuddyWorkKit:BEGIN id=deploy-target -->'
$END_MARK = '<!-- CodeBuddyWorkKit:END -->'
$H = $env:USERPROFILE

$ruleTargets = @(
  @{ label = 'CodeBuddy';  path = "$H\.codebuddy\rules\deploy-target.mdc"; mode = 'file' },
  @{ label = 'Cursor';     path = "$H\.cursor\rules\deploy-target.mdc";    mode = 'file' },
  @{ label = 'Windsurf';   path = "$H\.windsurf\rules\deploy-target.md";   mode = 'file' },
  @{ label = 'Claude Code'; path = "$H\.claude\CLAUDE.md";                 mode = 'block' },
  @{ label = 'Codex';      path = "$H\.codex\AGENTS.md";                   mode = 'block' },
  @{ label = 'Cline';      path = "$H\.clinerules";                        mode = 'block' },
  @{ label = '通用';       path = (Join-Path $Target 'AGENTS.md');          mode = 'block' }
)

$removedRules = 0
foreach ($item in $ruleTargets) {
  try {
    if (-not (Test-Path $item.path)) { continue }

    if ($item.mode -eq 'file') {
      Remove-Item $item.path -Force
      $removedRules += 1
      continue
    }

    $text = [System.IO.File]::ReadAllText($item.path, [System.Text.Encoding]::UTF8)
    if ($text -notmatch [regex]::Escape($BEGIN_MARK)) { continue }

    $pattern = '(?s)' + [regex]::Escape($BEGIN_MARK) + '.*?' + [regex]::Escape($END_MARK)
    $cleaned = [regex]::Replace($text, $pattern, '').TrimEnd()

    if ([string]::IsNullOrWhiteSpace($cleaned)) {
      # 文件里只有我们的区块，整个删掉
      Remove-Item $item.path -Force
    } else {
      [System.IO.File]::WriteAllText($item.path, $cleaned + "`n", (New-Object System.Text.UTF8Encoding $false))
    }
    $removedRules += 1
  } catch {
    Add-Step ("规则 " + $item.label) 'fail' $_.Exception.Message
  }
}

if ($removedRules -gt 0) {
  Add-Step '部署规则' 'done' ("已从 {0} 个平台移除受管规则（其它规则未动）" -f $removedRules)
} else {
  Add-Step '部署规则' 'skip' '未发现受管规则'
}

# ------------------------------------------------------------------ 4. 目录
if (-not (Test-Path $Target)) {
  Add-Step '部署目录' 'skip' '目录已不存在'
} elseif ($KeepData) {
  # 保留 config / data / state，删掉应用与工具
  foreach ($sub in @('apps', 'tools', 'runtime', 'docs')) {
    $path = Join-Path $Target $sub
    if (Test-Path $path) {
      try { Remove-Item $path -Recurse -Force; } catch { }
    }
  }
  Add-Step '部署目录' 'done' '已删除应用与工具，保留 config / data / state'
} else {
  $canDelete = $true
  try {
    Get-ChildItem $Target -Force | Where-Object { $_.Name -ne '一键清理与回退.cmd' } | ForEach-Object {
      Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
    }
  } catch {
    $canDelete = $false
    Add-Step '部署目录' 'skip' ('部分文件被占用：' + $_.Exception.Message)
  }

  if ($canDelete) {
    if ($FromInside) {
      # 自己就在这个目录里运行：交给后台进程延迟删除
      Start-Process -FilePath 'cmd.exe' `
        -ArgumentList '/c', 'timeout', '2', '>nul', '&', 'rmdir', '/s', '/q', ("`"" + $Target + "`"") `
        -WindowStyle Hidden
      Add-Step '部署目录' 'done' '已交给后台删除（几秒后消失）'
    } else {
      try {
        Remove-Item $Target -Recurse -Force -ErrorAction Stop
        Add-Step '部署目录' 'done' '已删除'
      } catch {
        Add-Step '部署目录' 'skip' ('残留：' + $_.Exception.Message)
      }
    }
  }
}

# ------------------------------------------------------------------ 汇总
$fail = @($steps | Where-Object { $_.status -eq 'fail' })

if ($Json) {
  [pscustomobject]@{
    ok    = ($fail.Count -eq 0)
    target = $Target
    steps = $steps
  } | ConvertTo-Json -Depth 5 -Compress
} else {
  Write-Host ''
  Write-Host '  --------------------------------------------' -ForegroundColor DarkGray
  if ($fail.Count -eq 0) {
    Write-Host '  回退完成：服务已停、快捷方式与规则已清、目录已处理' -ForegroundColor Green
  } else {
    Write-Host ("  回退完成，但有 {0} 项失败，请手动检查" -f $fail.Count) -ForegroundColor Yellow
  }
  Write-Host ''
}

if ($fail.Count -gt 0) { exit 1 } else { exit 0 }
