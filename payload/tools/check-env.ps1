# ============================================================================
# check-env.ps1 - 部署环境检测
#
# 在安装前运行：确认这台机器能否正常跑起本套件。任何一项 BLOCK 都会阻止安装；
# WARN 只提示，不影响继续。退出码：0 = 可以安装，1 = 存在阻塞项。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File check-env.ps1
#   powershell -ExecutionPolicy Bypass -File check-env.ps1 -Json
#   powershell -ExecutionPolicy Bypass -File check-env.ps1 -Target "C:\Kit" -Port 47910
# ============================================================================
param(
  [string]$Target = "$env:LOCALAPPDATA\CodeBuddyWorkKit",
  [int]$Port = 47910,
  [int]$PulsePort = 47821,
  [int]$TunerPort = 47822,
  [switch]$Json
)

$ErrorActionPreference = 'Stop'
$checks = @()

function Add-Check {
  param([string]$Name, [string]$Status, [string]$Detail, [string]$Fix = '')

  $script:checks += [pscustomobject]@{
    name   = $Name
    status = $Status   # pass | warn | block
    detail = $Detail
    fix    = $Fix
  }
}

function Test-PortFree {
  param([int]$P)

  $conn = Get-NetTCPConnection -LocalPort $P -State Listen -ErrorAction SilentlyContinue
  return (-not $conn)
}

# ---------------------------------------------------------------- PowerShell
$ver = $PSVersionTable.PSVersion
if ($ver.Major -ge 5) {
  Add-Check 'PowerShell 版本' 'pass' ("Windows PowerShell {0}.{1}" -f $ver.Major, $ver.Minor)
} else {
  Add-Check 'PowerShell 版本' 'block' ("当前 {0}.{1}，需要 5.0 及以上" -f $ver.Major, $ver.Minor) '安装 WMF 5.1 后重试'
}

# ---------------------------------------------------------------- Node.js
$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
$bundledNode = Join-Path $PSScriptRoot '..\runtime\node.exe'
if (-not (Test-Path $bundledNode)) { $bundledNode = $null }

if ($nodeCmd) {
  try {
    $raw = (& $nodeCmd.Source -v).Trim()
    $major = [int]($raw.TrimStart('v').Split('.')[0])
    if ($major -ge 18) {
      Add-Check 'Node.js' 'pass' ("{0} @ {1}" -f $raw, $nodeCmd.Source)
    } else {
      Add-Check 'Node.js' 'block' ("{0} 版本过低，需要 18 及以上" -f $raw) '安装 Node 18+ 或使用随包 runtime\node.exe'
    }
  } catch {
    Add-Check 'Node.js' 'warn' '检测到 node 但无法读取版本' '确认 node -v 可正常执行'
  }
} elseif ($bundledNode) {
  $raw = (& $bundledNode -v).Trim()
  $major = [int]($raw.TrimStart('v').Split('.')[0])
  if ($major -ge 18) {
    Add-Check 'Node.js' 'pass' ("随包运行时 {0}（系统未安装 node）" -f $raw)
  } else {
    Add-Check 'Node.js' 'block' ("随包运行时 {0} 版本过低" -f $raw) '替换 runtime\node.exe 为 18+'
  }
} else {
  Add-Check 'Node.js' 'block' '未找到 node，且随包未附带 runtime\node.exe' '安装 Node 18+ 或使用含运行时的安装包'
}

# ---------------------------------------------------------------- .NET / 安装器
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (Test-Path $csc) {
  Add-Check '.NET Framework' 'pass' '4.x 运行时可用（安装器依赖）'
} else {
  Add-Check '.NET Framework' 'warn' '未找到 csc.exe，安装器仍可运行（仅重新编译时需要）'
}

# ---------------------------------------------------------------- 目标目录
try {
  $parent = Split-Path $Target -Parent
  if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $probe = Join-Path $parent ('.write-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  [System.IO.File]::WriteAllText($probe, 'ok')
  Remove-Item $probe -Force
  Add-Check '目标目录可写' 'pass' $Target
} catch {
  Add-Check '目标目录可写' 'block' ("无法写入 {0}：{1}" -f $Target, $_.Exception.Message) '改用有写权限的目录，或以管理员身份运行'
}

$drive = (Split-Path $Target -Qualifier)
try {
  $free = (Get-PSDrive -Name $drive.TrimEnd(':') -ErrorAction Stop).Free / 1MB
  if ($free -ge 300) {
    Add-Check '磁盘空间' 'pass' ("{0} 盘剩余 {1:N0} MB" -f $drive, $free)
  } else {
    Add-Check '磁盘空间' 'warn' ("{0} 盘剩余 {1:N0} MB，建议至少 300 MB" -f $drive, $free) '清理磁盘后重试'
  }
} catch {
  Add-Check '磁盘空间' 'warn' '无法读取磁盘剩余空间'
}

# ---------------------------------------------------------------- 端口
foreach ($item in @(@{ n = '索引库'; p = $Port }, @{ n = '进度看板'; p = $PulsePort }, @{ n = '降温面板'; p = $TunerPort })) {
  if (Test-PortFree $item.p) {
    Add-Check ("端口 " + $item.p + "（" + $item.n + "）") 'pass' '空闲'
  } else {
    $owner = (Get-NetTCPConnection -LocalPort $item.p -State Listen -ErrorAction SilentlyContinue |
      Select-Object -First 1 -ExpandProperty OwningProcess)
    $name = ''
    if ($owner) {
      $proc = Get-Process -Id $owner -ErrorAction SilentlyContinue
      if ($proc) { $name = $proc.ProcessName }
    }
    Add-Check ("端口 " + $item.p + "（" + $item.n + "）") 'warn' ("已被占用" + $(if ($name) { "，进程 $name($owner)" } else { '' })) '安装时可改用其它端口，或先关闭占用程序'
  }
}

# ---------------------------------------------------------------- 已有部署
$existing = Join-Path $Target 'config\deploy.json'
if (Test-Path $existing) {
  Add-Check '已有部署' 'warn' ("目标目录已存在部署：{0}" -f $Target) '继续安装会覆盖应用代码，配置与数据保留'
} else {
  Add-Check '已有部署' 'pass' '目标目录为空，首次安装'
}

# ---------------------------------------------------------------- 规则目录
$rulesDir = Join-Path $env:USERPROFILE '.codebuddy\rules'
if (Test-Path $rulesDir) {
  Add-Check '规则目录' 'pass' $rulesDir
} else {
  Add-Check '规则目录' 'warn' ('不存在：{0}（部署时自动创建）' -f $rulesDir)
}

# ---------------------------------------------------------------- 汇总
$blocks = @($checks | Where-Object { $_.status -eq 'block' })
$warns = @($checks | Where-Object { $_.status -eq 'warn' })

if ($Json) {
  [pscustomobject]@{
    ok       = ($blocks.Count -eq 0)
    blocks   = $blocks.Count
    warns    = $warns.Count
    target   = $Target
    checks   = $checks
  } | ConvertTo-Json -Depth 5 -Compress
} else {
  Write-Host ''
  Write-Host '  部署环境检测' -ForegroundColor Cyan
  Write-Host '  ============================================' -ForegroundColor DarkGray
  foreach ($c in $checks) {
    $tag = switch ($c.status) {
      'pass' { '通过' }
      'warn' { '提示' }
      default { '阻塞' }
    }
    $color = switch ($c.status) {
      'pass' { 'Green' }
      'warn' { 'Yellow' }
      default { 'Red' }
    }
    Write-Host ("  [{0}] {1}" -f $tag, $c.name) -ForegroundColor $color -NoNewline
    Write-Host ("  {0}" -f $c.detail) -ForegroundColor DarkGray
    if ($c.fix -and $c.status -ne 'pass') {
      Write-Host ("         建议：{0}" -f $c.fix) -ForegroundColor DarkYellow
    }
  }
  Write-Host ''
  if ($blocks.Count -eq 0) {
    Write-Host ("  结论：可以安装（{0} 项提示）" -f $warns.Count) -ForegroundColor Green
  } else {
    Write-Host ("  结论：存在 {0} 项阻塞，请先处理" -f $blocks.Count) -ForegroundColor Red
  }
  Write-Host ''
}

if ($blocks.Count -gt 0) { exit 1 } else { exit 0 }
