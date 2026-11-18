# Connect external Antigravity to the selected MK Billing installation.
param(
  [string]$DataDirectory,
  [string]$ConfigPath,
  [string]$Url = "http://127.0.0.1:47320/mcp",
  [ValidateSet('stdio','http')][string]$Transport = 'stdio',
  [switch]$Demo
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $DataDirectory) {
  $DataDirectory = if ($Demo -or -not (Test-Path (Join-Path $root 'publish\MK.Hq.Api.exe'))) {
    Join-Path $root 'src\MK.Hq.Api\var-dev'
  } else { Join-Path $root 'publish\var' }
}
if (-not $ConfigPath) { $ConfigPath = Join-Path $root '.agents\mcp_config.json' }
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$endpoint = [Uri]$Url
if (-not $endpoint.IsAbsoluteUri -or -not $endpoint.IsLoopback -or $endpoint.Scheme -ne 'http' -or $endpoint.AbsolutePath -ne '/mcp' -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.UserInfo) {
  throw 'Use the local HTTP MCP endpoint, for example http://127.0.0.1:47320/mcp.'
}
# Validate existing configuration before creating credentials or changing files.
$config = if (Test-Path -LiteralPath $ConfigPath) {
  Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
if ($null -eq $config -or $config -isnot [pscustomobject]) { throw 'MCP config must be a JSON object.' }
if (-not $config.PSObject.Properties['mcpServers']) { $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) }
if ($config.mcpServers -isnot [pscustomobject]) { throw 'mcpServers must be a JSON object.' }
[void][IO.Directory]::CreateDirectory($DataDirectory)
$tokenPath = Join-Path $DataDirectory 'mcp.token'
if (-not (Test-Path -LiteralPath $tokenPath)) {
  $bytes = New-Object byte[] 24
  $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
  $newToken = ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
  # CreateNew preserves a token created concurrently by the server.
  try {
    $stream = [IO.File]::Open($tokenPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $encoded = [Text.Encoding]::UTF8.GetBytes($newToken); $stream.Write($encoded, 0, $encoded.Length) } finally { $stream.Dispose() }
  } catch [IO.IOException] { if (-not (Test-Path -LiteralPath $tokenPath)) { throw } }
}
$token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
if ($token.Length -lt 32) { throw 'The MCP token file is invalid. Restart MK Billing to recreate it.' }
$server = if ($Transport -eq 'http') { [pscustomobject]@{
  serverUrl = $endpoint.AbsoluteUri
  headers = [pscustomobject]@{ Authorization = "Bearer $token" }
  timeoutSeconds = 180
} } else {
  if ($endpoint.AbsoluteUri -ne 'http://127.0.0.1:47320/mcp') { throw 'The local stdio MCP uses port 47320. Use -Transport http for another endpoint.' }
  $shellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
  $launchArgs = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'mcp-stdio.ps1'), '-DataDirectory', $DataDirectory)
  if ($Demo) { $launchArgs += '-Demo' }
  [pscustomobject]@{ command=$shellPath; args=$launchArgs; timeoutSeconds=240 }
}
$config.mcpServers | Add-Member -NotePropertyName 'mk-billing' -NotePropertyValue $server -Force
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $ConfigPath))
if (Test-Path -LiteralPath $ConfigPath) { Copy-Item -LiteralPath $ConfigPath -Destination ($ConfigPath + '.bak') -Force }
$temporary = $ConfigPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
  [IO.File]::WriteAllText($temporary, ($config | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
  Move-Item -LiteralPath $temporary -Destination $ConfigPath -Force
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
Write-Host "Configured mk-billing in $ConfigPath"

# Antigravity loads local rules and keeps tool schemas for both desktop and CLI.
$rulesSource = Join-Path $root 'docs\ANTIGRAVITY.md'
$rulesText = Get-Content -LiteralPath $rulesSource -Raw
$geminiDirectory = Join-Path $env:USERPROFILE '.gemini'
$backupDirectory = Join-Path $root ('artifacts\antigravity-setup-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
[void][IO.Directory]::CreateDirectory($backupDirectory)
$pluginDirectory = Join-Path $geminiDirectory 'config\plugins\mk-shop'
if (Test-Path -LiteralPath $pluginDirectory) {
  Copy-Item -LiteralPath $pluginDirectory -Destination (Join-Path $backupDirectory 'mk-shop') -Recurse
  [void][IO.Directory]::CreateDirectory((Join-Path $pluginDirectory 'rules'))
  [IO.File]::WriteAllText((Join-Path $pluginDirectory 'rules\AGENTS.md'), $rulesText, [Text.UTF8Encoding]::new($false))
  # Engineering history stays in repository docs, outside the shop agent's rule context.
  $oldHistory = Join-Path $pluginDirectory 'rules\HISTORY_LEARNING.md'
  if (Test-Path -LiteralPath $oldHistory) { Remove-Item -LiteralPath $oldHistory }
  $installationPath = Join-Path $pluginDirectory 'installation.json'
  if (Test-Path -LiteralPath $installationPath) {
    $installation = Get-Content -LiteralPath $installationPath -Raw | ConvertFrom-Json
    $installation.PSObject.Properties.Remove('sourceRoot')
    $installation.PSObject.Properties.Remove('consolidatedApi')
    $installation | Add-Member -NotePropertyName mcpServerName -NotePropertyValue 'mk-billing' -Force
    [IO.File]::WriteAllText($installationPath, ($installation | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
  }
}
foreach ($surface in @('antigravity', 'antigravity-cli')) {
  $surfaceDirectory = Join-Path $geminiDirectory $surface
  [void][IO.Directory]::CreateDirectory($surfaceDirectory)
  $agentsPath = Join-Path $surfaceDirectory 'AGENTS.md'
  $existing = if (Test-Path -LiteralPath $agentsPath) {
    Copy-Item -LiteralPath $agentsPath -Destination (Join-Path $backupDirectory ($surface + '-AGENTS.md'))
    Get-Content -LiteralPath $agentsPath -Raw
  } else { '' }
  $block = "<!-- mk-shop:start -->`n" + $rulesText.Trim() + "`n<!-- mk-shop:end -->"
  $pattern = '(?s)<!-- mk-shop:start -->.*?<!-- mk-shop:end -->'
  $updated = if ([regex]::IsMatch($existing, $pattern)) {
    [regex]::Replace($existing, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $block })
  } else { $existing.TrimEnd() + "`n`n" + $block + "`n" }
  [IO.File]::WriteAllText($agentsPath, $updated, [Text.UTF8Encoding]::new($false))
}

try {
  $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json, text/event-stream' }
  $init = Invoke-RestMethod $endpoint.AbsoluteUri -Method Post -Headers $headers -ContentType 'application/json' -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"mk-setup","version":"1"}}}' -TimeoutSec 10
  $discovery = Invoke-RestMethod $endpoint.AbsoluteUri -Method Post -Headers $headers -ContentType 'application/json' -Body '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' -TimeoutSec 10
  $names = @($discovery.result.tools | ForEach-Object name)
  if (($names -join ',') -ne 'scan,lookup_items,post_bill') { throw 'The running server does not expose the three-tool billing contract.' }
  foreach ($surface in @('antigravity', 'antigravity-cli')) {
    $cache = [IO.Path]::GetFullPath((Join-Path $geminiDirectory "$surface\mcp\mk-billing"))
    $cacheRoot = [IO.Path]::GetFullPath((Join-Path $geminiDirectory "$surface\mcp"))
    if (-not $cache.StartsWith($cacheRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cache path validation failed.' }
    if (Test-Path -LiteralPath $cache) { Copy-Item -LiteralPath $cache -Destination (Join-Path $backupDirectory ($surface + '-mk-billing')) -Recurse }
    [void][IO.Directory]::CreateDirectory($cache)
    foreach ($stale in (Get-ChildItem -LiteralPath $cache -File -Filter '*.json')) { Remove-Item -LiteralPath $stale.FullName }
    $cachedInstructions = $init.result.instructions + "`n`n" + $rulesText
    [IO.File]::WriteAllText((Join-Path $cache 'instructions.md'), $cachedInstructions, [Text.UTF8Encoding]::new($false))
    foreach ($tool in $discovery.result.tools) {
      $cachedTool = @{ name = $tool.name; description = $tool.description; parameters = $tool.inputSchema }
      [IO.File]::WriteAllText((Join-Path $cache ($tool.name + '.json')), ($cachedTool | ConvertTo-Json -Depth 40 -Compress), [Text.UTF8Encoding]::new($false))
    }
  }
  Write-Host 'Updated desktop and CLI MCP caches with scan, lookup_items, post_bill.'
} catch {
  Write-Warning ("Rules updated. Start the matching MK Billing build and rerun setup to refresh tool caches. " + $_.Exception.Message)
}
Write-Host 'Updated Antigravity rules. Reload the MK Billing MCP connection in Antigravity.'
