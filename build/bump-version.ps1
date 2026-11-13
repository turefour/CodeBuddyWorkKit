# ============================================================================
# bump-version.ps1 - 升版本号 + 写更新日志（一处改，两处同步）
#
# 版本号的唯一来源是 payload\VERSION，但对外说明在 CHANGELOG.md。手改容易漏一处，
# 所以合成一条命令。改完记得重新打包，否则包里的 VERSION 还是旧的。
#
# 用法：
#   powershell -File build\bump-version.ps1 -Kind patch -Note "修复 XX 失效"
#   powershell -File build\bump-version.ps1 -Kind minor -Note "新增 ZZ 能力"
#   powershell -File build\bump-version.ps1 -Kind major -Note "部署结构改为 ..."
#   powershell -File build\bump-version.ps1 -Show          # 只看当前版本
#
# -Kind 判定标准：目标机上的「使用方式」有没有变
#   patch 修 bug / 改文案 / 调参数       1.0.0 -> 1.0.1
#   minor 新增功能 / 大范围优化          1.0.0 -> 1.1.0
#   major 不兼容（部署结构/配置格式）     1.0.0 -> 2.0.0
# ============================================================================
param(
  [ValidateSet('patch', 'minor', 'major', '')]
  [string]$Kind = '',
  [string]$Note = '',
  [switch]$Show
)

$ErrorActionPreference = 'Stop'

$KitRoot = Split-Path $PSScriptRoot -Parent
$VersionFile = Join-Path $KitRoot 'payload\VERSION'
$ChangeLog = Join-Path $KitRoot 'CHANGELOG.md'

function Say {
  param([string]$Text, [string]$Color = 'Gray')
  Write-Host $Text -ForegroundColor $Color
}

if (-not (Test-Path $VersionFile)) {
  Say ('  [fail] 找不到 ' + $VersionFile) 'Red'
  exit 2
}

$current = (Get-Content $VersionFile -Raw -Encoding UTF8).Trim()
if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
  Say ('  [fail] VERSION 内容不是 x.y.z 格式：' + $current) 'Red'
  exit 2
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

if ($Show -or [string]::IsNullOrEmpty($Kind)) {
  Say ''
  Say ('  当前版本：' + $current) 'Cyan'
  Say ('  文件：' + $VersionFile)
  Say ''
  Say '  升版本：powershell -File build\bump-version.ps1 -Kind patch|minor|major -Note "说明"' 'DarkGray'
  Say ''
  exit 0
}

switch ($Kind) {
  'patch' { $patch += 1 }
  'minor' { $minor += 1; $patch = 0 }
  'major' { $major += 1; $minor = 0; $patch = 0 }
}

$next = '{0}.{1}.{2}' -f $major, $minor, $patch

# VERSION 写无 BOM：它是给 Node 读的
[System.IO.File]::WriteAllText($VersionFile, $next + "`n", (New-Object System.Text.UTF8Encoding $false))

# CHANGELOG：插在第一条版本记录之前
$entry = @(
  ('## [' + $next + '] - ' + (Get-Date).ToString('yyyy-MM-dd')),
  '',
  ('- ' + $(if ($Note) { $Note } else { '(待补充)' })),
  ''
)

if (Test-Path $ChangeLog) {
  $lines = Get-Content $ChangeLog -Encoding UTF8
  $insertAt = -1
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[') { $insertAt = $i; break }
  }

  if ($insertAt -ge 0) {
    $new = @()
    $new += $lines[0..($insertAt - 1)]
    $new += $entry
    $new += '---'
    $new += ''
    $new += $lines[$insertAt..($lines.Count - 1)]
    [System.IO.File]::WriteAllLines($ChangeLog, $new, (New-Object System.Text.UTF8Encoding $true))
  } else {
    Add-Content -Path $ChangeLog -Value ($entry -join "`r`n") -Encoding UTF8
  }
}

Say ''
Say ('  [ok]   ' + $current + '  ->  ' + $next) 'Green'
Say ('         已写入 payload\VERSION 与 CHANGELOG.md') 'DarkGray'
Say ''
Say '  下一步：powershell -File build\build-package.ps1 -Out ...' 'Cyan'
Say ''
