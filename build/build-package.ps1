# ============================================================================
# build-package.ps1 - 生成可分发的安装包
#
# 做五件事：
#   1. 复制四个应用源码到 payload\apps（排除 node_modules / dist / logs 等产物）
#   2. 复制运行时（node.exe，可选便携 git）到 payload\runtime
#   3. 编译 installer.exe（系统自带 csc，无需安装任何工具）
#   4. 把 payload\tools 下的 ps1 统一转成 UTF-8 BOM（否则 PowerShell 5.1 按 GBK 解析会语法错）
#   5. 打包成 zip 并输出体积报告
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build\build-package.ps1
#   powershell -ExecutionPolicy Bypass -File build\build-package.ps1 -NoRuntime -Out "D:\kit.zip"
# ============================================================================
param(
  [string]$Out = '',
  [switch]$NoRuntime,
  [switch]$NoGit
)

$ErrorActionPreference = 'Stop'

$KitRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
$Payload = Join-Path $KitRoot 'payload'
$AppsDir = Join-Path $Payload 'apps'
$RuntimeDir = Join-Path $Payload 'runtime'

$Sources = @{
  codevault   = 'C:\Users\GH\Desktop\CodeVault'
  pulse       = 'C:\Users\GH\CodeBuddy\20261029081733\codebuddy-pulse'
  tuner       = 'C:\Users\GH\CodeBuddy\20261029081733\codebuddy-idle-tuner'
  scriptvault = 'C:\Users\GH\Desktop\ScriptVault'
}

$NodeExe = 'C:\Program Files\nodejs\node.exe'
$MinGit = 'C:\Users\GH\CodeBuddy\20261029081733\.tools\mingit'

# 按路径段排除：整串通配（'*\node_modules\*'）匹配不到顶层目录，会误带几百 MB
$ExcludeSegments = @(
  'node_modules', '.git', 'dist', 'build', 'logs', '.pulse',
  '安装包', 'screens', '.cache', 'coverage', '.tools', '.playwright-cli'
)
$ExcludeFiles = @('*.log', '*.zip', '*.pid', '*.tsbuildinfo', '*.bak_*', '.DS_Store', 'Thumbs.db', '*.tmp')

function Test-Skip {
  param([string]$RelativePath)

  $parts = $RelativePath -split '\\'
  for ($i = 0; $i -lt ($parts.Length - 1); $i++) {
    if ($ExcludeSegments -contains $parts[$i]) { return $true }
  }
  $leaf = $parts[$parts.Length - 1]
  foreach ($pattern in $ExcludeFiles) {
    if ($leaf -like $pattern) { return $true }
  }
  return $false
}

function Say {
  param([string]$Text, [string]$Color = 'Gray')
  Write-Host $Text -ForegroundColor $Color
}

function Copy-Filtered {
  param([string]$Source, [string]$Destination, [string]$Label)

  if (-not (Test-Path $Source)) { Say ("  [skip] {0}（源不存在）" -f $Label) 'DarkYellow'; return 0 }

  $head = (Resolve-Path $Source).Path.TrimEnd('\')
  $count = 0
  $bytes = 0

  Get-ChildItem $head -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($head.Length).TrimStart('\')
    if (Test-Skip $rel) { return }

    $out = Join-Path $Destination $rel
    $dir = Split-Path $out -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $_.FullName -Destination $out -Force
    $count += 1
    $bytes += $_.Length
  }

  Say ("  [ok]   {0,-12} {1,5} 个文件  {2,8:N1} MB" -f $Label, $count, ($bytes / 1MB)) 'Green'
  return $count
}

Say ''
Say '  CodeBuddy 工作套件 - 打包' 'Cyan'
Say '  ============================================' 'DarkGray'

# ------------------------------------------------------------------ 1. 清理并复制应用
Say ''
Say '  [1/5] 复制应用源码' 'Cyan'
if (Test-Path $AppsDir) { Remove-Item $AppsDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $AppsDir | Out-Null

$total = 0
foreach ($name in @('codevault', 'pulse', 'tuner', 'scriptvault')) {
  $total += Copy-Filtered -Source $Sources[$name] -Destination (Join-Path $AppsDir $name) -Label $name
}

# ------------------------------------------------------------------ 1b. 版本号
# VERSION 是版本检测的唯一来源：check-install.ps1 靠它判断「本包版本」，
# deploy.ps1 把它写进目标机的 deploy.json，两者才能比较。
Say ''
Say '  [1b/5] 写入版本号' 'Cyan'
$VersionFile = Join-Path $Payload 'VERSION'
$PackageVersion = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw -Encoding UTF8).Trim() } else { '0.0.0' }
[System.IO.File]::WriteAllText($VersionFile, $PackageVersion + "`n", (New-Object System.Text.UTF8Encoding $false))
Say ("  [ok]   VERSION = {0}" -f $PackageVersion) 'Green'

# ------------------------------------------------------------------ 1c. 清空本机数据
# 分发包不能带自己的项目索引与仓库清单：目标机上这些路径不存在，索引页会显示
# 一堆打不开的项目，也会把本机目录结构泄露出去。部署后由 adopt-project 重新登记。
Say ''
Say '  [1c/5] 清空本机私有数据' 'Cyan'

$projectsFile = Join-Path $AppsDir 'codevault\data\projects.json'
if (Test-Path $projectsFile) {
  $empty = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('yyyy-MM-dd')
    projects    = @()
  }
  [System.IO.File]::WriteAllText($projectsFile, ($empty | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))
  Say '  [ok]   codevault\data\projects.json -> 空索引（部署后用 adopt-project 登记）' 'Green'
}

$reposFile = Join-Path $AppsDir 'scriptvault\config\repos.json'
if (Test-Path $reposFile) {
  $empty = [pscustomobject]@{
    '$comment'  = '仓库清单：部署后由 tools/adopt-project.mjs 登记'
    updatedAt   = (Get-Date).ToString('yyyy-MM-dd')
    repos       = @()
  }
  [System.IO.File]::WriteAllText($reposFile, ($empty | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))
  Say '  [ok]   scriptvault\config\repos.json -> 空清单（部署后用 adopt-project 登记）' 'Green'
}

# ------------------------------------------------------------------ 2. 运行时
Say ''
Say '  [2/5] 复制运行时' 'Cyan'
if (Test-Path $RuntimeDir) { Remove-Item $RuntimeDir -Recurse -Force }

if ($NoRuntime) {
  Say '  [skip] 已指定 -NoRuntime（目标机需自备 Node 18+）' 'DarkYellow'
} else {
  New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
  if (Test-Path $NodeExe) {
    Copy-Item $NodeExe (Join-Path $RuntimeDir 'node.exe') -Force
    Say ("  [ok]   node.exe  {0:N1} MB  {1}" -f ((Get-Item $NodeExe).Length / 1MB), (& $NodeExe -v)) 'Green'
  } else {
    Say '  [warn] 未找到 node.exe，跳过（目标机需自备 Node 18+）' 'Yellow'
  }

  if (-not $NoGit) {
    if (Test-Path $MinGit) {
      $gitDst = Join-Path $RuntimeDir 'git'
      New-Item -ItemType Directory -Force -Path $gitDst | Out-Null
      Get-ChildItem $MinGit -Force | Where-Object { $_.Name -notmatch '^\.git' } | ForEach-Object {
        Copy-Item $_.FullName -Destination $gitDst -Recurse -Force
      }
      $gitSize = (Get-ChildItem $gitDst -Recurse -File -Force | Measure-Object Length -Sum).Sum
      Say ("  [ok]   git (MinGit)  {0:N1} MB" -f ($gitSize / 1MB)) 'Green'
    } else {
      Say '  [warn] 未找到便携 git，跳过（目标机需自备 git）' 'Yellow'
    }
  }
}

# ------------------------------------------------------------------ 3. PyShell 脚本转 BOM
Say ''
Say '  [3/5] 统一 PowerShell 脚本编码（UTF-8 BOM）' 'Cyan'
$bom = New-Object System.Text.UTF8Encoding $true
$converted = 0
Get-ChildItem (Join-Path $Payload 'tools') -Filter *.ps1 -File | ForEach-Object {
  $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
  [System.IO.File]::WriteAllText($_.FullName, $text, $bom)
  $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
  if ($bytes[0] -eq 0xEF) { $converted += 1 }
}
Say ("  [ok]   {0} 个脚本已带 BOM" -f $converted) 'Green'

# ------------------------------------------------------------------ 4. 编译安装器与桌面壳
Say ''
Say '  [4/5] 编译安装器与桌面壳' 'Cyan'

# 测试桌面壳时它会在 payload\shell\state 下落一个 Edge profile（近 80MB），
# 这属于运行时数据，绝不能进分发包。
$shellState = Join-Path $Payload 'shell\state'
if (Test-Path $shellState) {
  Remove-Item $shellState -Recurse -Force
  Say '  [clean] 移除 payload\shell\state（运行时残留）' 'DarkYellow'
}
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll')

$exe = Join-Path $KitRoot 'installer.exe'
if (Test-Path $exe) { Remove-Item $exe -Force }
& $csc /nologo /target:winexe /out:$exe @refs (Join-Path $KitRoot 'installer\Installer.cs') 2>&1 |
  ForEach-Object { Say ("  " + $_) 'Red' }
if (Test-Path $exe) {
  Say ("  [ok]   installer.exe  {0:N0} KB" -f ((Get-Item $exe).Length / 1KB)) 'Green'
} else {
  throw '安装器编译失败，打包中止。'
}

# 桌面壳：独立窗口 + 托盘
$shellDir = Join-Path $Payload 'shell'
New-Item -ItemType Directory -Force -Path $shellDir | Out-Null
$shellExe = Join-Path $shellDir 'CodeBuddyWorkKit.Shell.exe'
if (Test-Path $shellExe) { Remove-Item $shellExe -Force }
& $csc /nologo /target:winexe /out:$shellExe @refs (Join-Path $shellDir 'Shell.cs') 2>&1 |
  ForEach-Object { Say ("  " + $_) 'Red' }
if (Test-Path $shellExe) {
  Say ("  [ok]   CodeBuddyWorkKit.Shell.exe  {0:N0} KB" -f ((Get-Item $shellExe).Length / 1KB)) 'Green'
} else {
  throw '桌面壳编译失败，打包中止。'
}

# 原生窗口：纯 WinForms，不依赖浏览器
$nativeDir = Join-Path $Payload 'native'
$nativeExe = Join-Path $nativeDir 'CodeBuddyWorkKit.Native.exe'
if (Test-Path $nativeExe) { Remove-Item $nativeExe -Force }
& $csc /nologo /target:winexe /out:$nativeExe @refs `
  (Join-Path $nativeDir 'Theme.cs') `
  (Join-Path $nativeDir 'Api.cs') `
  (Join-Path $nativeDir 'NativeForm.cs') `
  (Join-Path $nativeDir 'Program.cs') 2>&1 |
  ForEach-Object { Say ("  " + $_) 'Red' }
if (Test-Path $nativeExe) {
  Say ("  [ok]   CodeBuddyWorkKit.Native.exe  {0:N0} KB" -f ((Get-Item $nativeExe).Length / 1KB)) 'Green'
} else {
  throw '原生窗口编译失败，打包中止。'
}

# ------------------------------------------------------------------ 5. 打包
Say ''
# 更新日志带进包：目标机上想知道「这一版有什么」时不该再回来翻仓库
$changelog = Join-Path $KitRoot 'CHANGELOG.md'
if (Test-Path $changelog) {
  $docsDir = Join-Path $Payload 'docs'
  New-Item -ItemType Directory -Force -Path $docsDir | Out-Null
  Copy-Item $changelog (Join-Path $docsDir '更新日志.md') -Force
  Say '  [ok]   docs\更新日志.md' 'Green'
} else {
  Say '  [warn] 未找到 CHANGELOG.md' 'Yellow'
}

Say '  [5/5] 生成压缩包' 'Cyan'

if ($Out -eq '') {
  $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CodeBuddyWorkKit-1.0.0.zip'
}
if (Test-Path $Out) { Remove-Item $Out -Force }

$staging = Join-Path $env:TEMP ('kit-stage-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

Copy-Item $exe (Join-Path $staging 'installer.exe') -Force
Copy-Item $Payload (Join-Path $staging 'payload') -Recurse -Force
Copy-Item (Join-Path $KitRoot 'README.txt') (Join-Path $staging 'README.txt') -Force -ErrorAction SilentlyContinue

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $Out -CompressionLevel Optimal -Force
Remove-Item $staging -Recurse -Force

$zipSize = (Get-Item $Out).Length / 1MB
Say ("  [ok]   {0}  {1:N1} MB" -f $Out, $zipSize) 'Green'
Say ''
Say '  打包完成。分发时把 zip 给对方，解压后双击 installer.exe。' 'White'
Say ''
