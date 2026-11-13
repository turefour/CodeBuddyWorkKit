# ============================================================================
# health-check.ps1 - 部署后的环境监测（文件完整性 + 功能完整性）
#
# 和另外三个脚本的分工：
#   check-env.ps1     安装【前】检查机器能不能装
#   deploy.ps1        执行安装
#   test-deploy.ps1   安装【后】跑一轮端到端自检
#   health-check.ps1  【随时】检查现在还好不好用（本文件）
#
# 设计原则：只读，不改任何东西。发现问题只报告，修复交给 -Repair 或人工。
#
# 用法：
#   powershell -File health-check.ps1 -Target "D:\Kit"
#   powershell -File health-check.ps1 -Target "D:\Kit" -Json
#   powershell -File health-check.ps1 -Target "D:\Kit" -Repair    # 尝试重启服务
#
# 退出码：0 = 全部通过；1 = 有 FAIL；2 = 目录不存在
# ============================================================================
param(
  [Parameter(Mandatory = $true)][string]$Target,
  [switch]$Json,
  [switch]$Repair,
  [int]$TimeoutMs = 2500
)

$ErrorActionPreference = 'Continue'
$Target = [System.IO.Path]::GetFullPath($Target)

if (-not (Test-Path $Target)) {
  if ($Json) {
    [pscustomobject]@{ ok = $false; error = 'target not found'; target = $Target } | ConvertTo-Json -Compress
  } else {
    Write-Host ("  部署目录不存在：{0}" -f $Target) -ForegroundColor Red
  }
  exit 2
}

$checks = New-Object System.Collections.Generic.List[object]

function Add-Result {
  param(
    [string]$Group,
    [string]$Name,
    [string]$Status,   # pass | fail | warn
    [string]$Detail = ''
  )
  $script:checks.Add([pscustomobject]@{ group = $Group; name = $Name; status = $Status; detail = $Detail })
}

# ---------------------------------------------------------------- 配置
$configFile = Join-Path $Target 'config\deploy.json'
$config = $null
if (Test-Path $configFile) {
  try {
    # 显式 UTF8：PS 5.1 按 GBK 读 UTF-8 会把中文注释解坏
    $config = Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Result '文件' 'config\deploy.json' 'pass' ("端口 {0}/{1}/{2}" -f $config.ports.codevault, $config.ports.pulse, $config.ports.tuner)
  } catch {
    Add-Result '文件' 'config\deploy.json' 'fail' ('无法解析：' + $_.Exception.Message)
  }
} else {
  Add-Result '文件' 'config\deploy.json' 'fail' '缺失'
}

$portCodevault = if ($config -and $config.ports) { [int]$config.ports.codevault } else { 47910 }
$portPulse = if ($config -and $config.ports) { [int]$config.ports.pulse } else { 47821 }
$portTuner = if ($config -and $config.ports) { [int]$config.ports.tuner } else { 47822 }

# ---------------------------------------------------------------- 一、文件完整性
$required = @(
  @{ rel = 'tools\launch.mjs'; label = '启动器' },
  @{ rel = 'tools\stop.mjs'; label = '停止器' },
  @{ rel = 'apps\codevault\tools\serve.mjs'; label = '索引库服务' },
  @{ rel = 'apps\codevault\index.html'; label = '索引库页面' },
  @{ rel = 'apps\codevault\assets\app.js'; label = '索引库脚本' },
  @{ rel = 'apps\codevault\data\projects.json'; label = '项目索引' },
  @{ rel = 'apps\pulse\server\pulse-server.js'; label = '看板服务' },
  @{ rel = 'apps\pulse\server\lib\dupguard.js'; label = '重复进程守护' },
  @{ rel = 'apps\pulse\web\index.html'; label = '看板页面' },
  @{ rel = 'apps\tuner\server\tuner-server.js'; label = '降温服务' },
  @{ rel = 'apps\tuner\web\index.html'; label = '降温页面' },
  @{ rel = 'apps\scriptvault\build-index.mjs'; label = '脚本库索引' },
  @{ rel = 'apps\scriptvault\tools\adopt-project.mjs'; label = '项目接入工具' },
  @{ rel = 'apps\scriptvault\config\repos.json'; label = '仓库清单' }
)

$missing = 0
foreach ($item in $required) {
  $full = Join-Path $Target $item.rel
  if (Test-Path $full) {
    $size = (Get-Item $full).Length
    if ($size -eq 0) {
      Add-Result '文件' $item.label 'fail' ($item.rel + ' 是空文件')
      $missing += 1
    } else {
      Add-Result '文件' $item.label 'pass' $item.rel
    }
  } else {
    Add-Result '文件' $item.label 'fail' ($item.rel + ' 缺失')
    $missing += 1
  }
}

# 运行时：随包 node 或系统 node
$runtimeNode = Join-Path $Target 'runtime\node.exe'
$nodeExe = $null
if (Test-Path $runtimeNode) {
  $nodeExe = $runtimeNode
  Add-Result '文件' '便携 Node' 'pass' ('runtime\node.exe ' + [math]::Round((Get-Item $runtimeNode).Length / 1MB, 1) + ' MB')
} else {
  $sys = Get-Command node -ErrorAction SilentlyContinue
  if ($sys) {
    $nodeExe = $sys.Source
    Add-Result '文件' '便携 Node' 'warn' ('未随包携带，使用系统 node：' + $sys.Source)
  } else {
    Add-Result '文件' '便携 Node' 'fail' '既无 runtime\node.exe 也无系统 node'
  }
}

# 便携 git（可选，缺失只警告：部分功能需要）
$runtimeGit = Join-Path $Target 'runtime\git\cmd\git.exe'
if (Test-Path $runtimeGit) {
  Add-Result '文件' '便携 Git' 'pass' 'runtime\git\cmd\git.exe'
} else {
  $sysGit = Get-Command git -ErrorAction SilentlyContinue
  if ($sysGit) {
    Add-Result '文件' '便携 Git' 'warn' ('未随包携带，使用系统 git：' + $sysGit.Source)
  } else {
    Add-Result '文件' '便携 Git' 'warn' '没有 git，版本管理相关功能不可用'
  }
}

# 部署规则
$ruleFile = Join-Path $env:USERPROFILE '.codebuddy\rules\deploy-target.mdc'
if (Test-Path $ruleFile) {
  Add-Result '文件' '部署规则' 'pass' 'deploy-target.mdc'
} else {
  Add-Result '文件' '部署规则' 'warn' '规则未写入（-NoRules 安装或已被清理）'
}

# ---------------------------------------------------------------- 二、功能完整性
function Test-Endpoint {
  param([string]$Url, [string]$Label, [switch]$NeedJson)

  try {
    $resp = Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec ([math]::Max(1, [int]($TimeoutMs / 1000)))
    if ($resp.StatusCode -ne 200) {
      Add-Result '功能' $Label 'fail' ("HTTP " + $resp.StatusCode)
      return $false
    }
    if ($NeedJson) {
      try {
        $null = $resp.Content | ConvertFrom-Json
      } catch {
        Add-Result '功能' $Label 'fail' '返回内容不是合法 JSON'
        return $false
      }
    }
    Add-Result '功能' $Label 'pass' ("HTTP 200 · " + [math]::Round($resp.RawContentLength / 1KB, 1) + ' KB')
    return $true
  } catch {
    Add-Result '功能' $Label 'fail' ($_.Exception.Message.Split([char]10)[0])
    return $false
  }
}

$okCodevault = Test-Endpoint -Url ("http://127.0.0.1:" + $portCodevault + "/api/health") -Label '索引库服务'
if ($okCodevault) {
  $null = Test-Endpoint -Url ("http://127.0.0.1:" + $portCodevault + "/api/repos") -Label '索引库仓库接口' -NeedJson
  $null = Test-Endpoint -Url ("http://127.0.0.1:" + $portCodevault + "/api/scripts") -Label '索引库脚本接口' -NeedJson
}
$okPulse = Test-Endpoint -Url ("http://127.0.0.1:" + $portPulse + "/api/health") -Label '看板服务'
if ($okPulse) {
  $null = Test-Endpoint -Url ("http://127.0.0.1:" + $portPulse + "/api/guard") -Label '重复进程守护接口' -NeedJson
}
$okTuner = Test-Endpoint -Url ("http://127.0.0.1:" + $portTuner + "/api/health") -Label '降温服务'

# 端口监听是否与配置一致
foreach ($pair in @(@{ p = $portCodevault; n = '索引库' }, @{ p = $portPulse; n = '看板' }, @{ p = $portTuner; n = '降温' })) {
  $listen = Get-NetTCPConnection -LocalPort $pair.p -State Listen -ErrorAction SilentlyContinue
  if ($listen) {
    Add-Result '功能' ($pair.n + '端口监听') 'pass' ("端口 " + $pair.p)
  } else {
    Add-Result '功能' ($pair.n + '端口监听') 'fail' ("端口 " + $pair.p + " 未监听")
  }
}

# 数据文件可解析（防被手工改坏）
$projectsFile = Join-Path $Target 'apps\codevault\data\projects.json'
if (Test-Path $projectsFile) {
  try {
    $data = Get-Content $projectsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Result '功能' '项目索引可解析' 'pass' ("{0} 个项目" -f @($data.projects).Count)
  } catch {
    Add-Result '功能' '项目索引可解析' 'fail' 'JSON 解析失败（可能被手工编辑坏了）'
  }
}

$reposFile = Join-Path $Target 'apps\scriptvault\config\repos.json'
if (Test-Path $reposFile) {
  try {
    $data = Get-Content $reposFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Result '功能' '仓库清单可解析' 'pass' ("{0} 个仓库" -f @($data.repos).Count)
  } catch {
    Add-Result '功能' '仓库清单可解析' 'fail' 'JSON 解析失败'
  }
}

# ---------------------------------------------------------------- 三、环境
$psv = $PSVersionTable.PSVersion
if ($psv.Major -ge 5) {
  Add-Result '环境' 'PowerShell 版本' 'pass' ("{0}.{1}" -f $psv.Major, $psv.Minor)
} else {
  Add-Result '环境' 'PowerShell 版本' 'fail' ("{0}.{1} 过低，需 5.1+" -f $psv.Major, $psv.Minor)
}

try {
  $drive = (Get-Item $Target).PSDrive
  $freeGB = [math]::Round($drive.Free / 1GB, 1)
  if ($freeGB -ge 1) {
    Add-Result '环境' '磁盘剩余' 'pass' ($freeGB.ToString() + ' GB')
  } else {
    Add-Result '环境' '磁盘剩余' 'warn' ($freeGB.ToString() + ' GB，偏低')
  }
} catch {
  Add-Result '环境' '磁盘剩余' 'warn' '无法读取'
}

try {
  $probe = Join-Path $Target ('.write-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  [System.IO.File]::WriteAllText($probe, 'ok')
  Remove-Item $probe -Force
  Add-Result '环境' '目录可写' 'pass' $Target
} catch {
  Add-Result '环境' '目录可写' 'fail' $_.Exception.Message
}

# ---------------------------------------------------------------- 修复（可选）
$repaired = $false
if ($Repair -and $nodeExe) {
  $down = @()
  if (-not $okCodevault) { $down += 'codevault' }
  if (-not $okPulse) { $down += 'pulse' }
  if (-not $okTuner) { $down += 'tuner' }

  if ($down.Count -gt 0) {
    foreach ($app in $down) {
      try {
        & $nodeExe (Join-Path $Target 'tools\launch.mjs') $app 2>&1 | Out-Null
      } catch {
        /* report below reflects reality */
      }
    }
    Start-Sleep -Seconds 3
    $repaired = $true
    Add-Result '修复' '尝试重启' 'warn' ('已尝试重启：' + ($down -join ', '))
  }
}

# ---------------------------------------------------------------- 输出
$pass = @($checks | Where-Object { $_.status -eq 'pass' }).Count
$fail = @($checks | Where-Object { $_.status -eq 'fail' }).Count
$warn = @($checks | Where-Object { $_.status -eq 'warn' }).Count

if ($Json) {
  [pscustomobject]@{
    ok       = ($fail -eq 0)
    target   = $Target
    ports    = @{ codevault = $portCodevault; pulse = $portPulse; tuner = $portTuner }
    pass     = $pass
    fail     = $fail
    warn     = $warn
    repaired = $repaired
    checks   = $checks
  } | ConvertTo-Json -Depth 5 -Compress
} else {
  Write-Host ''
  Write-Host '  环境监测' -ForegroundColor Cyan
  Write-Host '  ============================================' -ForegroundColor DarkGray
  Write-Host ('  目录：' + $Target)
  Write-Host ('  端口：{0} / {1} / {2}' -f $portCodevault, $portPulse, $portTuner)
  Write-Host ''

  $lastGroup = ''
  foreach ($item in $checks) {
    if ($item.group -ne $lastGroup) {
      Write-Host ('  [' + $item.group + ']') -ForegroundColor DarkGray
      $lastGroup = $item.group
    }
    $tag = switch ($item.status) {
      'pass' { '[通过]' }
      'warn' { '[提示]' }
      default { '[失败]' }
    }
    $color = switch ($item.status) {
      'pass' { 'Green' }
      'warn' { 'Yellow' }
      default { 'Red' }
    }
    Write-Host ('    ' + $tag + ' ' + $item.name.PadRight(18) + $item.detail) -ForegroundColor $color
  }

  Write-Host ''
  Write-Host ('  合计：通过 {0}　提示 {1}　失败 {2}' -f $pass, $warn, $fail) -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
  if ($fail -eq 0) {
    Write-Host '  结论：功能完整，可正常使用。' -ForegroundColor Green
  } else {
    Write-Host '  结论：存在失败项，请按提示处理；可加 -Repair 尝试自动重启服务。' -ForegroundColor Red
  }
  Write-Host ''
}

if ($fail -gt 0) { exit 1 }
exit 0
