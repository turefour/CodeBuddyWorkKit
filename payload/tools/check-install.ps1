# ============================================================================
# check-install.ps1 - 安装状态与版本检测（安装前调用）
#
# 回答三个问题：
#   1. 这台机器有没有装过本套件？装在哪、什么时候、什么版本？
#   2. 本安装包的版本是多少？
#   3. 两者的关系：未安装 / 同版本 / 可升级 / 降级
#
# 判断依据（按优先级）：
#   a. 注册表 HKCU\Software\CodeBuddyWorkKit 记录的安装路径（覆盖"装到别处"的情况）
#   b. -Target 指定目录下的 config\deploy.json
#   c. 安装包根目录的 VERSION 文件
#
# 退出码：0=未安装  1=已装同版本  2=已装旧版本（可升级）  3=已装新版本（降级）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File check-install.ps1 -Target "D:\Kit"
#   powershell -ExecutionPolicy Bypass -File check-install.ps1 -Target "D:\Kit" -Json
# ============================================================================
param(
  [string]$Target = "$env:LOCALAPPDATA\CodeBuddyWorkKit",
  [string]$PackageRoot = '',
  [switch]$Json
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
  $PackageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path)
}
$Target = [System.IO.Path]::GetFullPath($Target)

$REG_PATH = 'HKCU:\Software\CodeBuddyWorkKit'

function Read-PackageVersion {
  $file = Join-Path $PackageRoot 'VERSION'
  if (Test-Path $file) {
    $text = (Get-Content $file -Raw -Encoding UTF8).Trim()
    if ($text) { return $text }
  }
  # 兜底：从模板里取
  $tpl = Join-Path $PackageRoot 'config\deploy.template.json'
  if (Test-Path $tpl) {
    try {
      $data = Get-Content $tpl -Raw -Encoding UTF8 | ConvertFrom-Json
      if ($data.version) { return [string]$data.version }
    } catch { }
  }
  return '0.0.0'
}

function Read-InstallAt {
  <#
    读取某个目录下的安装信息；没有 deploy.json 就返回 $null。
    deploy.json 由 PowerShell 写出（无 BOM UTF-8），这里显式指定编码，
    否则中文路径/注释会被按 GBK 解码。
  #>
  param([string]$Dir)

  $configFile = Join-Path $Dir 'config\deploy.json'
  if (-not (Test-Path $configFile)) { return $null }

  try {
    $cfg = Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
  } catch {
    return [pscustomobject]@{
      path       = $Dir
      version    = 'unknown'
      deployedAt = ''
      ports      = $null
      broken     = $true
      error      = $_.Exception.Message
    }
  }

  return [pscustomobject]@{
    path       = $Dir
    version    = $(if ($cfg.version) { [string]$cfg.version } else { 'unknown' })
    deployedAt = $(if ($cfg.deployedAt) { [string]$cfg.deployedAt } else { '' })
    ports      = $cfg.ports
    broken     = $false
    error      = ''
  }
}

# ---------------------------------------------------------------- 收集已装位置
$found = New-Object System.Collections.Generic.List[object]

# 1) 注册表记录的路径（可能是另一个目录）
$regPath = $null
if (Test-Path $REG_PATH) {
  try {
    $props = Get-ItemProperty -Path $REG_PATH -ErrorAction Stop
    if ($props.InstallPath) { $regPath = [string]$props.InstallPath }
  } catch { }
}
if ($regPath -and (Test-Path $regPath)) {
  $info = Read-InstallAt -Dir $regPath
  if ($info) { $found.Add($info) }
}

# 2) 本次目标目录
$current = Read-InstallAt -Dir $Target
if ($current) {
  $alreadyListed = $found | Where-Object { $_.path -ieq $current.path }
  if (-not $alreadyListed) { $found.Add($current) }
}

# ---------------------------------------------------------------- 比较版本
$packageVersion = Read-PackageVersion
$primary = $null
if ($current) { $primary = $current }
elseif ($found.Count -gt 0) { $primary = $found[0] }

$state = 'not-installed'
$detail = '未检测到已安装的副本'

if ($primary) {
  # 安装时间要一起报出来：用户判断「该不该升级」看的往往是它离现在多久
  $deployedRaw = if ($primary.deployedAt) { [string]$primary.deployedAt } else { '' }
  $deployedText = '时间未知'
  if ($deployedRaw) {
    try {
      $deployedText = ([datetime]$deployedRaw).ToString('yyyy-MM-dd HH:mm')
    } catch {
      $deployedText = $deployedRaw
    }
  }

  try {
    $pv = [version]$packageVersion
    $iv = [version]$primary.version
    if ($iv -eq $pv) {
      $state = 'same'
      $detail = ("已安装 {0}，部署于 {1}（{2}）—— 与本包同版本" -f $primary.version, $deployedText, $primary.path)
    } elseif ($iv -lt $pv) {
      $state = 'upgrade'
      $detail = ("已安装 {0}，部署于 {1}（{2}）；本包 {3}，可升级" -f $primary.version, $deployedText, $primary.path, $packageVersion)
    } else {
      $state = 'downgrade'
      $detail = ("已安装 {0}，部署于 {1}（{2}）；高于本包 {3}，属于降级" -f $primary.version, $deployedText, $primary.path, $packageVersion)
    }
  } catch {
    $state = 'same'
    $detail = ("已安装副本版本无法比较（{0}，部署于 {1}）" -f $primary.version, $deployedText)
  }
}

$exitCode = switch ($state) {
  'same' { 1 }
  'upgrade' { 2 }
  'downgrade' { 3 }
  default { 0 }
}

# ---------------------------------------------------------------- 输出
# 可读摘要总是打印：安装器把这里当成日志显示，-Json 只是额外在末尾加一行
# 机器可读的 JSON，两者共存，调用方不必跑两遍。
if ($Json) {
  [pscustomobject]@{
    ok             = $true
    packageVersion = $packageVersion
    state          = $state
    detail         = $detail
    target         = $Target
    installed      = $primary
    allInstalls    = $found
    registryPath   = $REG_PATH
    registryValue  = $regPath
  } | ConvertTo-Json -Depth 6 -Compress
} else {
  Write-Host ''
  Write-Host '  安装状态检测' -ForegroundColor Cyan
  Write-Host '  ============================================' -ForegroundColor DarkGray
  Write-Host ("  目标目录 : {0}" -f $Target)
  Write-Host ("  本包版本 : {0}" -f $packageVersion) -ForegroundColor White

  if ($found.Count -eq 0) {
    Write-Host '  已安装   : 否' -ForegroundColor Green
  } else {
    foreach ($item in $found) {
      $tag = if ($item.broken) { '（配置损坏）' } else { '' }
      Write-Host ("  已安装   : {0}  版本 {1}  部署于 {2}{3}" -f $item.path, $item.version, $item.deployedAt, $tag)
    }
    if ($regPath -and $regPath -ine $Target) {
      Write-Host ("  注意     : 注册表记录的位置与目标目录不同（{0}）" -f $regPath) -ForegroundColor Yellow
    }
  }

  $color = switch ($state) {
  'not-installed' { 'Green' }
  'same' { 'Yellow' }
  'upgrade' { 'Green' }
  'downgrade' { 'Red' }
  }

  if ($primary) {
    $shown = '未知'
    if ($primary.deployedAt) {
      try {
        $shown = ([datetime]$primary.deployedAt).ToString('yyyy-MM-dd HH:mm')
      } catch {
        $shown = [string]$primary.deployedAt
      }
    }
    Write-Host ("  已装版本 : {0}" -f $primary.version) -ForegroundColor White
    Write-Host ("  更新日期 : {0}" -f $shown) -ForegroundColor White
  }
  Write-Host ''
  Write-Host ("  结论     : {0}" -f $detail) -ForegroundColor $color
  Write-Host ''
}

exit $exitCode
