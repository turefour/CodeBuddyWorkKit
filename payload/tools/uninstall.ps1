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

# ------------------------------------------------------------------ 3. 规则
$ruleFile = Join-Path $env:USERPROFILE '.codebuddy\rules\deploy-target.mdc'
try {
  if (Test-Path $ruleFile) {
    Remove-Item $ruleFile -Force
    Add-Step '部署规则' 'done' 'deploy-target.mdc 已移除（其它规则未动）'
  } else {
    Add-Step '部署规则' 'skip' '不存在'
  }
} catch {
  Add-Step '部署规则' 'fail' $_.Exception.Message
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
