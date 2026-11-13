# ============================================================================
# test-deploy.ps1 - 部署完成后的自检
#
# 只有全部 PASS 才算部署成功。检测分三层：
#   1. 文件层：目录结构、配置可解析、启动脚本与规则文件齐全
#   2. 进程层：按配置的端口启动三个服务
#   3. 接口层：HTTP 健康检查返回预期内容（用 app 字段区分是不是别的程序占了端口）
#
# 端口已被别的实例占用时记 SKIP（不算失败），避免在开发机上重复部署时误报。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File test-deploy.ps1 -Target "D:\Kit"
#   powershell -ExecutionPolicy Bypass -File test-deploy.ps1 -Target "D:\Kit" -Json
# ============================================================================
param(
  [string]$Target = "$env:LOCALAPPDATA\CodeBuddyWorkKit",
  [switch]$Json,
  [switch]$SkipStart
)

$ErrorActionPreference = 'Stop'
$Target = [System.IO.Path]::GetFullPath($Target)
$results = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Collections.Generic.List[string]

function Add-Result {
  param([string]$Name, [string]$Status, [string]$Detail)
  $script:results.Add([pscustomobject]@{ name = $Name; status = $Status; detail = $Detail })
  if (-not $Json) {
    $tag = switch ($Status) { 'pass' { 'PASS' } 'skip' { 'SKIP' } default { 'FAIL' } }
    $color = switch ($Status) { 'pass' { 'Green' } 'skip' { 'DarkYellow' } default { 'Red' } }
    Write-Host ("  [{0}] {1}" -f $tag, $Name) -ForegroundColor $color -NoNewline
    Write-Host ("  {0}" -f $Detail) -ForegroundColor DarkGray
  }
  $script:log.Add("[$Status] $Name - $Detail")
}

function Test-PortBusy {
  param([int]$Port)
  return [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Read-Health {
  param([string]$Url)
  try {
    $res = Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 4
    $body = $null
    try { $body = $res.Content | ConvertFrom-Json } catch { }
    return @{ ok = ($res.StatusCode -eq 200); body = $body }
  } catch {
    return @{ ok = $false; body = $null }
  }
}

# ------------------------------------------------------------------ 1. 文件层
if (-not $Json) {
  Write-Host ''
  Write-Host '  部署自检' -ForegroundColor Cyan
  Write-Host '  ============================================' -ForegroundColor DarkGray
}

$configFile = Join-Path $Target 'config\deploy.json'
$config = $null
if (Test-Path $configFile) {
  try {
    $config = Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Result '配置文件' 'pass' ("端口 {0}/{1}/{2}，监听 {3}" -f $config.ports.codevault, $config.ports.pulse, $config.ports.tuner, $config.host)
  } catch {
    Add-Result '配置文件' 'fail' ('无法解析 deploy.json：' + $_.Exception.Message)
  }
} else {
  Add-Result '配置文件' 'fail' ('缺少 ' + $configFile)
}

foreach ($app in @('codevault', 'pulse', 'tuner', 'scriptvault')) {
  $dir = Join-Path $Target ('apps\' + $app)
  $count = if (Test-Path $dir) { (Get-ChildItem $dir -Recurse -File -Force | Measure-Object).Count } else { 0 }
  if ($count -gt 0) {
    Add-Result ('应用 ' + $app) 'pass' ($count.ToString() + ' 个文件')
  } else {
    Add-Result ('应用 ' + $app) 'fail' '目录为空或缺失'
  }
}

foreach ($file in @('启动索引库.cmd', '启动全部服务.cmd', '停止全部服务.cmd', '一键清理与回退.cmd', 'tools\launch.mjs', 'tools\stop.mjs')) {
  $path = Join-Path $Target $file
  if (Test-Path $path) { Add-Result ('文件 ' + $file) 'pass' '存在' }
  else { Add-Result ('文件 ' + $file) 'fail' '缺失' }
}

$ruleFile = Join-Path $env:USERPROFILE '.codebuddy\rules\deploy-target.mdc'
if (Test-Path $ruleFile) {
  $size = (Get-Item $ruleFile).Length
  Add-Result '规则文件' 'pass' ('deploy-target.mdc（' + $size + ' 字节）')
} else {
  Add-Result '规则文件' 'fail' ('缺少 ' + $ruleFile)
}

$nodeExe = Join-Path $Target 'runtime\node.exe'
if (Test-Path $nodeExe) {
  Add-Result '运行时' 'pass' ('随包 node，' + [math]::Round((Get-Item $nodeExe).Length / 1MB, 1) + ' MB')
} elseif (Get-Command node -ErrorAction SilentlyContinue) {
  Add-Result '运行时' 'pass' '使用系统 node'
} else {
  Add-Result '运行时' 'fail' '既无随包 node 也无系统 node'
}

# ------------------------------------------------------------------ 2/3. 进程与接口
if ($SkipStart) {
  Add-Result '服务启动' 'skip' '已指定 -SkipStart'
} elseif (-not $config) {
  Add-Result '服务启动' 'fail' '缺少配置，无法启动'
} else {
  $node = if (Test-Path $nodeExe) { $nodeExe } else { 'node' }
  $launch = Join-Path $Target 'tools\launch.mjs'
  $stop = Join-Path $Target 'tools\stop.mjs'

  if (-not $Json) { Write-Host '' }
  try {
    $out = & $node $launch all --no-open --timeout 30 2>&1
    foreach ($line in $out) { $script:log.Add([string]$line) }
  } catch {
    Add-Result '服务启动' 'fail' ($_.Exception.Message)
  }

  $checks = @(
    @{ name = '索引库 health'; url = ('http://127.0.0.1:' + $config.ports.codevault + '/api/health'); port = $config.ports.codevault; app = 'codevault' },
    @{ name = '索引库 repos'; url = ('http://127.0.0.1:' + $config.ports.codevault + '/api/repos'); port = $config.ports.codevault; app = '' },
    @{ name = '索引库 scripts'; url = ('http://127.0.0.1:' + $config.ports.codevault + '/api/scripts'); port = $config.ports.codevault; app = '' },
    @{ name = '进度看板 health'; url = ('http://127.0.0.1:' + $config.ports.pulse + '/api/health'); port = $config.ports.pulse; app = '' },
    @{ name = '降温面板 health'; url = ('http://127.0.0.1:' + $config.ports.tuner + '/api/health'); port = $config.ports.tuner; app = '' }
  )

  foreach ($check in $checks) {
    $health = Read-Health $check.url
    if ($health.ok) {
      if ($check.app -and $health.body -and $health.body.app -and $health.body.app -ne $check.app) {
        Add-Result $check.name 'fail' ('端口 ' + $check.port + ' 被其它程序占用（app=' + $health.body.app + '）')
      } else {
        Add-Result $check.name 'pass' ('HTTP 200，端口 ' + $check.port)
      }
    } elseif (Test-PortBusy $check.port) {
      Add-Result $check.name 'skip' ('端口 ' + $check.port + ' 被占用但非本服务')
    } else {
      Add-Result $check.name 'fail' ('无响应：' + $check.url)
    }
  }

  # 收尾：停掉刚才启动的服务（不影响端口上原有的实例）
  try {
    & $node $stop all 2>&1 | ForEach-Object { $script:log.Add([string]$_) }
  } catch {
    # stop failures must not fail the report
  }
}

# ------------------------------------------------------------------ 汇总
$fail = @($results | Where-Object { $_.status -eq 'fail' })
$pass = @($results | Where-Object { $_.status -eq 'pass' })
$skip = @($results | Where-Object { $_.status -eq 'skip' })

if ($Json) {
  [pscustomobject]@{
    ok      = ($fail.Count -eq 0)
    pass    = $pass.Count
    fail    = $fail.Count
    skip    = $skip.Count
    results = $results
    log     = $log
  } | ConvertTo-Json -Depth 5 -Compress
} else {
  Write-Host ''
  Write-Host '  --------------------------------------------' -ForegroundColor DarkGray
  if ($fail.Count -eq 0) {
    Write-Host ("  自检通过：{0} 项通过，{1} 项跳过" -f $pass.Count, $skip.Count) -ForegroundColor Green
  } else {
    Write-Host ("  自检失败：{0} 项失败，{1} 项通过" -f $fail.Count, $pass.Count) -ForegroundColor Red
    foreach ($f in $fail) { Write-Host ('    - ' + $f.name + '：' + $f.detail) -ForegroundColor Red }
  }
  Write-Host ''
}

if ($fail.Count -gt 0) { exit 1 } else { exit 0 }
