# ============================================================================
# deploy.ps1 - 实际部署逻辑（安装器调用，也可手动运行）
#
# 做四件事：
#   1. 把 payload\apps 下的四个应用复制到目标目录（排除 node_modules / logs / dist 等产物）
#   2. 写入 config\deploy.json（端口、监听地址、运行时可执行文件）
#   3. 生成 启动/停止 脚本与桌面快捷方式
#   4. 写入 ~\.codebuddy\rules\deploy-target.mdc，让本机 CodeBuddy 知道这套东西在哪
#
# 幂等：重复部署只覆盖应用代码，保留 config/deploy.json 与 data/ 下的数据，
#       除非显式传 -ResetConfig。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File deploy.ps1 -Target "D:\CodeBuddyWorkKit"
#   powershell -ExecutionPolicy Bypass -File deploy.ps1 -Target ... -Port 50000 -Lan
# ============================================================================
param(
  [string]$Target = "$env:LOCALAPPDATA\CodeBuddyWorkKit",
  [int]$Port = 47910,
  [int]$PulsePort = 47821,
  [int]$TunerPort = 47822,
  [switch]$Lan,
  [switch]$NoDesktop,
  [switch]$NoRules,
  [switch]$ResetConfig,
  # 规则分发目标：auto（只为已装平台写，默认）| all | codebuddy,codex,claude,cursor,windsurf,cline,generic
  [string]$Platforms = 'auto',
  [switch]$Json
)

$ErrorActionPreference = 'Stop'

$PayloadRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
$Target = [System.IO.Path]::GetFullPath($Target)

# 版本号唯一来源：payload\VERSION（build 时写入）。写在 deploy.json 里，
# 下次安装时 check-install.ps1 就能比对出「同版本 / 可升级 / 降级」。
$VersionFile = Join-Path $PayloadRoot 'VERSION'
$PackageVersion = if (Test-Path $VersionFile) {
  (Get-Content $VersionFile -Raw -Encoding UTF8).Trim()
} else {
  '1.0.0'
}

$REG_PATH = 'HKCU:\Software\CodeBuddyWorkKit'
$log = New-Object System.Collections.Generic.List[string]

function Say {
  param([string]$Text, [string]$Color = 'Gray')
  $script:log.Add($Text)
  if (-not $Json) { Write-Host $Text -ForegroundColor $Color }
}

# 排除规则：按「路径段」匹配，而不是整串通配。
# 用 '*\node_modules\*' 这种写法匹配不到顶层的 node_modules（前面没有反斜杠），
# 会把 400 多 MB 依赖一起复制走。
$ExcludeSegments = @(
  'node_modules', '.git', 'dist', 'build', 'logs', '.pulse',
  '安装包', 'screens', '.cache', 'coverage', '.tools', '.playwright-cli'
)
$ExcludeFiles = @('*.log', '*.zip', '*.pid', '*.tsbuildinfo', '*.bak_*', '.DS_Store', 'Thumbs.db', '*.tmp')

function Test-Excluded {
  param([string]$RelativePath)

  $parts = $RelativePath -split '\\'
  # 目录段：除最后一段（文件名）外，任何一段命中就排除
  for ($i = 0; $i -lt ($parts.Length - 1); $i++) {
    if ($ExcludeSegments -contains $parts[$i]) { return $true }
  }
  $leaf = $parts[$parts.Length - 1]
  foreach ($pattern in $ExcludeFiles) {
    if ($leaf -like $pattern) { return $true }
  }
  return $false
}

function Copy-Tree {
  param([string]$Source, [string]$Destination, [string]$Label)

  if (-not (Test-Path $Source)) {
    Say ("  [skip] {0}（源不存在）" -f $Label) 'DarkYellow'
    return 0
  }

  $head = (Resolve-Path $Source).Path.TrimEnd('\')
  $copied = 0
  $bytes = 0

  Get-ChildItem $head -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($head.Length).TrimStart('\')
    if (Test-Excluded $rel) { return }
    $out = Join-Path $Destination $rel
    $dir = Split-Path $out -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $_.FullName -Destination $out -Force
    $copied += 1
    $bytes += $_.Length
  }

  Say ("  [ok]   {0}：{1} 个文件，{2:N1} MB" -f $Label, $copied, ($bytes / 1MB)) 'Green'
  return $copied
}

function Write-Launcher {
  param([string]$Name, [string]$Title, [string]$Command)

  $bat = @"
@echo off
chcp 65001 >nul
title $Title
cd /d "%~dp0"
set "NODE_EXE=%~dp0runtime\node.exe"
if not exist "%NODE_EXE%" set "NODE_EXE=node"
%NODE_EXE% $Command
if errorlevel 1 pause
"@
  $path = Join-Path $Target $Name
  [System.IO.File]::WriteAllText($path, ($bat -replace "`n", "`r`n"), (New-Object System.Text.UTF8Encoding $false))
  return $path
}

# ------------------------------------------------------------------ 1. 复制应用
Say ''
Say '  [1/5] 复制应用代码' 'Cyan'

$totalFiles = 0
foreach ($app in @('codevault', 'pulse', 'tuner', 'scriptvault')) {
  $src = Join-Path $PayloadRoot ("apps\" + $app)
  $dst = Join-Path $Target ("apps\" + $app)
  $totalFiles += Copy-Tree -Source $src -Destination $dst -Label $app
}

# tools / docs / rules 模板
$totalFiles += Copy-Tree -Source (Join-Path $PayloadRoot 'tools') -Destination (Join-Path $Target 'tools') -Label 'tools'
$totalFiles += Copy-Tree -Source (Join-Path $PayloadRoot 'docs') -Destination (Join-Path $Target 'docs') -Label 'docs'

# 桌面壳：把网页装进独立窗口 + 托盘常驻（浏览器嵌入版）
$shellSrc = Join-Path $PayloadRoot 'shell\CodeBuddyWorkKit.Shell.exe'
if (Test-Path $shellSrc) {
  Copy-Item $shellSrc (Join-Path $Target 'CodeBuddyWorkKit.Shell.exe') -Force
  Say '  [ok]   CodeBuddyWorkKit.Shell.exe（浏览器嵌入版）' 'Green'
  $totalFiles += 1
} else {
  Say '  [warn] 未找到桌面壳，将退回用浏览器打开' 'Yellow'
}

# 原生窗口（纯 WinForms，不依赖任何浏览器）
$nativeSrc = Join-Path $PayloadRoot 'native\CodeBuddyWorkKit.Native.exe'
if (Test-Path $nativeSrc) {
  Copy-Item $nativeSrc (Join-Path $Target 'CodeBuddyWorkKit.Native.exe') -Force
  Say '  [ok]   CodeBuddyWorkKit.Native.exe（原生窗口，无需浏览器）' 'Green'
  $totalFiles += 1
} else {
  Say '  [warn] 未找到原生窗口程序' 'Yellow'
}

# runtime（便携 node + git，可选）：整个目录一起带过去
$runtimeSrcDir = Join-Path $PayloadRoot 'runtime'
$runtimeDir = Join-Path $Target 'runtime'
$runtimePath = ''

if (Test-Path (Join-Path $runtimeSrcDir 'node.exe')) {
  New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
  $runtimeBytes = 0
  Get-ChildItem $runtimeSrcDir -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($runtimeSrcDir.Length).TrimStart('\')
    $out = Join-Path $runtimeDir $rel
    $dir = Split-Path $out -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $_.FullName -Destination $out -Force
    $runtimeBytes += $_.Length
  }
  $runtimePath = '"' + (Join-Path $runtimeDir 'node.exe') + '"'
  Say ("  [ok]   runtime（node{0}，共 {1:N1} MB）" -f $(if (Test-Path (Join-Path $runtimeDir 'git')) { ' + git' } else { '' }), ($runtimeBytes / 1MB)) 'Green'
  if (Test-Path (Join-Path $runtimeDir 'git\cmd\git.exe')) {
    Say '  [ok]   便携 git：runtime\git\cmd\git.exe' 'Green'
  }
} else {
  $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
  if ($nodeCmd) {
    $runtimePath = '"' + $nodeCmd.Source + '"'
    Say ("  [ok]   使用系统 node：{0}" -f $nodeCmd.Source) 'Green'
  } else {
    throw '既没有随包 runtime\node.exe，也没有系统 node，无法部署。'
  }
}

# ------------------------------------------------------------------ 2. 配置
Say ''
Say '  [2/5] 写入配置' 'Cyan'

$configDir = Join-Path $Target 'config'
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$configFile = Join-Path $configDir 'deploy.json'
$stateDir = Join-Path $Target 'state'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

if ((Test-Path $configFile) -and -not $ResetConfig) {
  Say '  [keep] config\deploy.json 已存在，保留原配置（-ResetConfig 可覆盖）' 'DarkYellow'
  $config = Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
  $oldVersion = [string]$config.version
  # 升级场景：端口/监听地址沿用，版本号与部署时间更新为新包
  if ($oldVersion -ne $PackageVersion) {
    $config.version = $PackageVersion
    $config.deployedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
    [System.IO.File]::WriteAllText(
      $configFile,
      ($config | ConvertTo-Json -Depth 5),
      (New-Object System.Text.UTF8Encoding $false)
    )
    Say ("  [up]   {0} -> {1}" -f $oldVersion, $PackageVersion) 'Green'
  }
} else {
  $config = [pscustomobject]@{
    version    = $PackageVersion
    host       = $(if ($Lan) { '0.0.0.0' } else { '127.0.0.1' })
    lan        = [bool]$Lan
    ports      = [pscustomobject]@{ codevault = $Port; pulse = $PulsePort; tuner = $TunerPort }
    apps       = @('codevault', 'pulse', 'tuner', 'scriptvault')
    rules      = @('deploy-target.mdc')
    runtime    = $runtimePath
    target     = $Target
    deployedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
  }
  # 必须写「无 BOM」的 UTF-8：Node 的 JSON.parse 遇到 BOM 会直接抛错，
  # 而 PS 5.1 的 Set-Content -Encoding UTF8 默认带 BOM。
  [System.IO.File]::WriteAllText(
    $configFile,
    ($config | ConvertTo-Json -Depth 5),
    (New-Object System.Text.UTF8Encoding $false)
  )
  Say ("  [ok]   config\deploy.json（端口 {0}/{1}/{2}，监听 {3}）" -f $Port, $PulsePort, $TunerPort, $config.host) 'Green'
}

# 仓库清单里先登记本套件自身：否则「仓库与备份」页面是空的，用户不知道能加什么
$reposFile = Join-Path $Target 'apps\scriptvault\config\repos.json'
if (Test-Path $reposFile) {
  try {
    $repoData = Get-Content $reposFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($repoData.repos).Count -eq 0) {
      $repoData.repos = @([pscustomobject]@{ name = 'workkit'; path = $Target; note = '本套件部署目录' })
      [System.IO.File]::WriteAllText(
        $reposFile,
        ($repoData | ConvertTo-Json -Depth 4),
        (New-Object System.Text.UTF8Encoding $false)
      )
      Say '  [ok]   仓库清单已登记部署目录（workkit）' 'Green'
    }
  } catch {
    Say ('  [warn] 仓库清单初始化失败：' + $_.Exception.Message) 'Yellow'
  }
}

# ------------------------------------------------------------------ 3. 启动脚本
Say ''
Say '  [3/5] 生成启动脚本' 'Cyan'

$launchers = @(
  @{ name = '启动索引库.cmd'; title = 'CodeVault 索引库'; cmd = 'tools\launch.mjs codevault' },
  @{ name = '启动全部服务.cmd'; title = 'CodeBuddy 全套服务'; cmd = 'tools\launch.mjs all' },
  @{ name = '停止全部服务.cmd'; title = '停止所有服务'; cmd = 'tools\stop.mjs all' }
)
foreach ($item in $launchers) {
  $path = Write-Launcher -Name $item.name -Title $item.title -Command $item.cmd
  Say ("  [ok]   {0}" -f $item.name) 'Green'
}

# 独立窗口入口（推荐）：Esc 不会误关，最小化进托盘
if (Test-Path (Join-Path $Target 'CodeBuddyWorkKit.Shell.exe')) {
  $winBat = @"
@echo off
chcp 65001 >nul
cd /d "%~dp0"
start "" "%~dp0CodeBuddyWorkKit.Shell.exe"
"@
  [System.IO.File]::WriteAllText((Join-Path $Target '启动独立窗口.cmd'), ($winBat -replace "`n", "`r`n"), (New-Object System.Text.UTF8Encoding $false))
  Say '  [ok]   启动独立窗口.cmd' 'Green'
}

# 一键清理
$cleanBat = @"
@echo off
chcp 65001 >nul
title 一键清理 / 回退部署
cd /d "%~dp0"
set "NODE_EXE=%~dp0runtime\node.exe"
if not exist "%NODE_EXE%" set "NODE_EXE=node"
echo 即将停止所有服务、删除桌面快捷方式、移除规则，并删除本目录全部内容。
echo 目标目录：$Target
echo.
choice /c YN /m "确认继续"
if errorlevel 2 goto :end
"%NODE_EXE%" tools\stop.mjs all
powershell -NoProfile -ExecutionPolicy Bypass -File tools\uninstall.ps1 -Target "$Target" -FromInside
:end
"@
$cleanPath = Join-Path $Target '一键清理与回退.cmd'
[System.IO.File]::WriteAllText($cleanPath, ($cleanBat -replace "`n", "`r`n"), (New-Object System.Text.UTF8Encoding $false))
Say '  [ok]   一键清理与回退.cmd' 'Green'

# 桌面快捷方式
if (-not $NoDesktop) {
  try {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $com = New-Object -ComObject WScript.Shell
    $linkPath = Join-Path $desktop 'CodeBuddy 工作套件.lnk'
    $link = $com.CreateShortcut($linkPath)

    $nativeExe = Join-Path $Target 'CodeBuddyWorkKit.Native.exe'
    $shellExe = Join-Path $Target 'CodeBuddyWorkKit.Shell.exe'

    if (Test-Path $nativeExe) {
      # 默认给原生版：启动最快、不依赖 Edge
      $link.TargetPath = $nativeExe
      $link.WindowStyle = 1
      $link.Description = 'CodeBuddy 工作套件（原生窗口）'

      # 再给一个浏览器嵌入版的快捷方式，两种形态都留着
      if (Test-Path $shellExe) {
        $link2 = $com.CreateShortcut((Join-Path $desktop 'CodeBuddy 工作套件（浏览器版）.lnk'))
        $link2.TargetPath = $shellExe
        $link2.WorkingDirectory = $Target
        $link2.WindowStyle = 1
        $link2.Description = 'CodeBuddy 工作套件（浏览器嵌入版）'
        $link2.Save()
      }
    } elseif (Test-Path $shellExe) {
      $link.TargetPath = $shellExe
      $link.WindowStyle = 1
      $link.Description = 'CodeBuddy 工作套件（独立窗口）'
    } else {
      $link.TargetPath = Join-Path $Target '启动索引库.cmd'
      $link.WindowStyle = 7
      $link.Description = 'CodeVault 索引库 + Pulse/Tuner 面板'
    }
    $link.WorkingDirectory = $Target
    $link.Save()
    Say '  [ok]   桌面快捷方式：CodeBuddy 工作套件' 'Green'
  } catch {
    Say ('  [warn] 桌面快捷方式创建失败：' + $_.Exception.Message) 'Yellow'
  }
}

# 明确不写任何开机启动项：只有手动双击快捷方式才会运行
Say '  [info] 未写入注册表 Run / 启动文件夹（不做开机自启）' 'DarkGray'

# ------------------------------------------------------------------ 4. 规则
Say ''
Say '  [4/5] 分发规则（多平台）' 'Cyan'
if (-not $NoRules) {
  # 规则正文只有一份，按平台落到各自的约定路径：
  #   CodeBuddy / Cursor -> 独占 .mdc；Codex / Claude / Cline -> 追加受管区块
  $installRules = Join-Path $Target 'tools\install-rules.ps1'
  if (Test-Path $installRules) {
    $ruleArgs = @(
      '-Target', $Target,
      '-PackageRoot', $PayloadRoot,
      '-PortCodevault', [string]$config.ports.codevault,
      '-PortPulse', [string]$config.ports.pulse,
      '-PortTuner', [string]$config.ports.tuner,
      '-BindAddress', [string]$config.host,
      '-Platforms', $Platforms
    )
    try {
      & powershell -NoProfile -ExecutionPolicy Bypass -File $installRules @ruleArgs
    } catch {
      Say ('  [warn] 规则分发失败：' + $_.Exception.Message) 'Yellow'
    }
  } else {
    Say '  [warn] install-rules.ps1 缺失，跳过' 'Yellow'
  }
} else {
  Say '  [skip] 已指定 -NoRules' 'DarkYellow'
}

# ------------------------------------------------------------------ 5. 完成
# 记录安装位置：否则换个目录再装一次，两边都不知道对方存在
try {
  New-Item -Path $REG_PATH -Force | Out-Null
  Set-ItemProperty -Path $REG_PATH -Name 'InstallPath' -Value $Target
  Set-ItemProperty -Path $REG_PATH -Name 'Version' -Value $PackageVersion
  Set-ItemProperty -Path $REG_PATH -Name 'InstalledAt' -Value (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
  Say ("  [ok]   已登记安装位置（{0}，版本 {1}）" -f $REG_PATH, $PackageVersion) 'Green'
} catch {
  Say ('  [warn] 注册表登记失败：' + $_.Exception.Message) 'Yellow'
}

Say ''
Say '  [5/5] 部署完成' 'Cyan'
Say ("  部署目录：{0}" -f $Target) 'White'
Say ("  访问地址：http://127.0.0.1:{0}/" -f $config.ports.codevault) 'White'
if ($config.lan) {
  $lanIp = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
    Select-Object -First 1 -ExpandProperty IPAddress)
  if ($lanIp) { Say ("  局域网访问：http://{0}:{1}/" -f $lanIp, $config.ports.codevault) 'White' }
}
Say ''

if ($Json) {
  [pscustomobject]@{
    ok         = $true
    target     = $Target
    files      = $totalFiles
    ports      = $config.ports
    host       = $config.host
    configFile = $configFile
    log        = $script:log
  } | ConvertTo-Json -Depth 5 -Compress
}

exit 0
