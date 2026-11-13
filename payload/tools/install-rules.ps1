# ============================================================================
# install-rules.ps1 - 把同一份规则正文分发到各 AI 编程平台的约定位置
#
# 为什么需要它：规则正文（索引库在哪、工具怎么调、收尾怎么提交）是平台无关的，
# 只有「文件放哪、叫什么」不同。把正文写一次、按平台落盘，就不必维护多份。
#
# 支持的落盘方式：
#   file  独占文件（.mdc / .md），卸载时直接删
#   block 追加到聚合文件（AGENTS.md / CLAUDE.md / .clinerules），
#         用 <!-- CodeBuddyWorkKit:BEGIN --> 包裹，升级替换、卸载删除，不碰用户原有内容
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File install-rules.ps1 -Target "D:\Kit"
#   powershell -ExecutionPolicy Bypass -File install-rules.ps1 -Target "D:\Kit" -Platforms codebuddy,codex
#   powershell -ExecutionPolicy Bypass -File install-rules.ps1 -Target "D:\Kit" -Platforms all
#
# -Platforms auto（默认）：只为「本机已存在的平台目录」写规则，另加部署目录一份通用 AGENTS.md
# ============================================================================
param(
  [Parameter(Mandatory = $true)][string]$Target,
  [string]$PackageRoot = '',
  [string]$Platforms = 'auto',
  [int]$PortCodevault = 47910,
  [int]$PortPulse = 47821,
  [int]$PortTuner = 47822,
  # 注意：不能叫 -Host，PowerShell 的 $Host 是只读内置变量，赋值会直接报错退出
  [string]$BindAddress = '127.0.0.1',
  [switch]$Json
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
  $PackageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path)
}
$Target = [System.IO.Path]::GetFullPath($Target)

$HOME_DIR = $env:USERPROFILE
$BEGIN_MARK = '<!-- CodeBuddyWorkKit:BEGIN id=deploy-target -->'
$END_MARK = '<!-- CodeBuddyWorkKit:END -->'

# ---------------------------------------------------------------- 平台表
# probe 用来判断「本机装没装这个平台」，避免给没装的东西建目录。
$PLATFORM_TABLE = [ordered]@{
  codebuddy = @{ label = 'CodeBuddy'; dir = "$HOME_DIR\.codebuddy\rules";  file = 'deploy-target.mdc'; mode = 'file';  probe = "$HOME_DIR\.codebuddy" }
  cursor    = @{ label = 'Cursor';    dir = "$HOME_DIR\.cursor\rules";    file = 'deploy-target.mdc'; mode = 'file';  probe = "$HOME_DIR\.cursor" }
  claude    = @{ label = 'Claude Code'; dir = "$HOME_DIR\.claude";        file = 'CLAUDE.md';         mode = 'block'; probe = "$HOME_DIR\.claude" }
  codex     = @{ label = 'Codex';     dir = "$HOME_DIR\.codex";           file = 'AGENTS.md';         mode = 'block'; probe = "$HOME_DIR\.codex" }
  windsurf  = @{ label = 'Windsurf';  dir = "$HOME_DIR\.windsurf\rules";  file = 'deploy-target.md';  mode = 'file';  probe = "$HOME_DIR\.windsurf" }
  cline     = @{ label = 'Cline';     dir = "$HOME_DIR";                  file = '.clinerules';       mode = 'block'; probe = "$HOME_DIR\.vscode\extensions" }
  generic   = @{ label = '通用（部署目录）'; dir = $Target;               file = 'AGENTS.md';         mode = 'block'; probe = $Target }
}

# ---------------------------------------------------------------- 正文
$tpl = Join-Path $PackageRoot 'rules\deploy-target.mdc'
if (-not (Test-Path $tpl)) {
  if ($Json) {
    [pscustomobject]@{ ok = $false; error = 'rules\deploy-target.mdc 缺失'; written = @() } | ConvertTo-Json -Compress
  } else {
    Write-Host '  [fail] 规则模板缺失：rules\deploy-target.mdc' -ForegroundColor Red
  }
  exit 2
}

$raw = Get-Content $tpl -Raw -Encoding UTF8
$raw = $raw.Replace('__TARGET__', $Target)
$raw = $raw.Replace('__PORT_CODEVAULT__', [string]$PortCodevault)
$raw = $raw.Replace('__PORT_PULSE__', [string]$PortPulse)
$raw = $raw.Replace('__PORT_TUNER__', [string]$PortTuner)
$raw = $raw.Replace('__HOST__', $BindAddress)
$raw = $raw.Replace('__BASE__', ('http://127.0.0.1:' + $PortCodevault))
$raw = $raw.Replace('__DEPLOYED__', (Get-Date).ToString('yyyy-MM-dd'))

# 聚合文件里不能有 YAML frontmatter，去掉后得到纯正文
$bodyOnly = ($raw -replace '(?s)^---\s*\r?\n.*?\r?\n---\s*\r?\n', '').Trim()

# ---------------------------------------------------------------- 选平台
$registry = $PLATFORM_TABLE.Keys | Where-Object { $_ -ne 'generic' }

$selected = @()
if ($Platforms -eq 'auto') {
  foreach ($key in $registry) {
    if (Test-Path $PLATFORM_TABLE[$key].probe) { $selected += $key }
  }
  # 一个都没检测到：至少给 CodeBuddy 和通用各写一份，免得装完没有任何指引
  if ($selected.Count -eq 0) { $selected = @('codebuddy') }
  $selected += 'generic'
} elseif ($Platforms -eq 'all') {
  $selected = @($registry) + 'generic'
} else {
  $selected = $Platforms.Split(',') | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ }
}

$unknown = $selected | Where-Object { -not $PLATFORM_TABLE.Contains($_) }
if ($unknown) {
  Write-Host ('  [fail] 未知平台：' + ($unknown -join ', ')) -ForegroundColor Red
  Write-Host ('         可选：' + (($PLATFORM_TABLE.Keys) -join ', ') + '，或 auto / all')
  exit 2
}

# ---------------------------------------------------------------- 写入
$written = New-Object System.Collections.Generic.List[object]
$utf8 = New-Object System.Text.UTF8Encoding $false

function Write-FileMode {
  param([string]$Key, [hashtable]$Spec)
  if (-not (Test-Path $Spec.dir)) { New-Item -ItemType Directory -Force -Path $Spec.dir | Out-Null }
  $path = Join-Path $Spec.dir $Spec.file
  [System.IO.File]::WriteAllText($path, $raw, $utf8)
  $script:written.Add([pscustomobject]@{ platform = $Key; label = $Spec.label; path = $path; mode = 'file' })
}

function Write-BlockMode {
  param([string]$Key, [hashtable]$Spec)
  if (-not (Test-Path $Spec.dir)) { New-Item -ItemType Directory -Force -Path $Spec.dir | Out-Null }
  $path = Join-Path $Spec.dir $Spec.file

  $existing = ''
  if (Test-Path $path) {
    $existing = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
  }

  $block = $BEGIN_MARK + "`n" + $bodyOnly + "`n" + $END_MARK

  if ($existing -match [regex]::Escape($BEGIN_MARK)) {
    # 已存在受管区块：整体替换，保留块外内容
    $pattern = '(?s)' + [regex]::Escape($BEGIN_MARK) + '.*?' + [regex]::Escape($END_MARK)
    $merged = [regex]::Replace($existing, $pattern, $block.Replace('$', '$$'))
  } else {
    $sep = if ($existing.Trim().Length -gt 0) { "`n`n" } else { '' }
    $merged = $existing.TrimEnd() + $sep + $block + "`n"
  }

  [System.IO.File]::WriteAllText($path, $merged, $utf8)
  $script:written.Add([pscustomobject]@{ platform = $Key; label = $Spec.label; path = $path; mode = 'block' })
}

foreach ($key in $selected) {
  $spec = $PLATFORM_TABLE[$key]
  if ($spec.mode -eq 'file') { Write-FileMode -Key $key -Spec $spec }
  else { Write-BlockMode -Key $key -Spec $spec }
}

# ---------------------------------------------------------------- 输出
if ($Json) {
  [pscustomobject]@{
    ok        = $true
    target    = $Target
    platforms = $selected
    written   = $written
  } | ConvertTo-Json -Depth 5 -Compress
} else {
  Write-Host ''
  Write-Host '  规则分发' -ForegroundColor Cyan
  Write-Host '  ============================================' -ForegroundColor DarkGray
  foreach ($item in $written) {
    $tag = if ($item.mode -eq 'file') { '独占文件' } else { '追加区块' }
    Write-Host ("  [ok]   {0,-14} {1}" -f $item.label, $item.path) -ForegroundColor Green
    Write-Host ("         {0}" -f $tag) -ForegroundColor DarkGray
  }
  Write-Host ''
  Write-Host ('  共写入 {0} 个平台。' -f $written.Count) -ForegroundColor White
  Write-Host '  提示：聚合文件里的内容被 <!-- CodeBuddyWorkKit:BEGIN --> 包裹，卸载时只删这一块。' -ForegroundColor DarkGray
  Write-Host ''
}

exit 0
