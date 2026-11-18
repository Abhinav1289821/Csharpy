# Local MCP transport owned by the MCP client. Stdout is JSON-RPC only.
param([string]$DataDirectory, [switch]$Demo)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Split-Path -Parent $PSScriptRoot
$endpoint = 'http://127.0.0.1:47320'
if (-not $DataDirectory) {
  $DataDirectory = if ($Demo -or -not (Test-Path (Join-Path $root 'publish/MK.Hq.Api.exe'))) {
    Join-Path $root 'src/MK.Hq.Api/var-dev'
  } else { Join-Path $root 'publish/var' }
}
Add-Type -AssemblyName System.Net.Http
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(185)
$probe = [Net.Http.HttpClient]::new($handler, $false)
$probe.Timeout = [TimeSpan]::FromSeconds(2)

function Test-McpHost {
  try {
    $response = $probe.GetAsync($endpoint + '/api/health').GetAwaiter().GetResult()
    try {
      if (-not $response.IsSuccessStatusCode) { return $false }
      $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
      return $health.app -eq 'mk-hq'
    } finally { $response.Dispose() }
  } catch { return $false }
}

function Connect-Mcp {
  if (Test-McpHost) { return }
  $mutex = [Threading.Mutex]::new($false, 'Local\MK-HQ-MCP-Start')
  $locked = $false
  try {
    try { $locked = $mutex.WaitOne(65000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'MCP connection is temporarily unavailable.' }
    if (-not (Test-McpHost)) {
      # All launcher output is private; no browser, shell window or stdout banner.
      & (Join-Path $PSScriptRoot 'start.ps1') -NoBrowser -Demo:$Demo *> $null
      if (-not (Test-McpHost)) { throw 'MCP connection is temporarily unavailable.' }
    }
  } finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
  }
}

function Send-Mcp([string]$line) {
  $token = (Get-Content -LiteralPath (Join-Path $DataDirectory 'mcp.token') -Raw).Trim()
  $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $endpoint + '/mcp')
  $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
  [void]$request.Headers.TryAddWithoutValidation('Accept', 'application/json, text/event-stream')
  $request.Content = [Net.Http.StringContent]::new($line, [Text.Encoding]::UTF8, 'application/json')
  try {
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    try {
      # A real HTTP response (including validation/auth errors) is never a reason to restart or replay.
      $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      if (-not $body.Trim()) {
        if (-not $response.IsSuccessStatusCode) { throw 'MCP request was rejected.' }
        return
      }
      # The endpoint returns compact JSON. Keep IDs, numbers, and image data intact.
      return $body.Trim()
    } finally { $response.Dispose() }
  } finally { $request.Dispose() }
}

try {
  while ($null -ne ($line = [Console]::ReadLine())) {
    if (-not $line.Trim()) { continue }
    $message = $null
    try { $message = $line | ConvertFrom-Json } catch {
      [Console]::WriteLine('{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Invalid JSON."}}')
      continue
    }
    try {
      Connect-Mcp
      # One replay for reads/initialization after a dropped connection. Financial calls
      # remain uncertain until the caller retries the SAME operationId and payload.
      try { $reply = Send-Mcp $line } catch {
        if ($message.method -notin @('initialize', 'ping', 'tools/list') -and
            -not ($message.method -eq 'tools/call' -and $message.params.name -eq 'lookup_items')) { throw }
        Connect-Mcp
        $reply = Send-Mcp $line
      }
      if ($reply -and $message.method -eq 'initialize') {
        $handshake = $reply | ConvertFrom-Json
        if ($handshake.result) {
          $handshake.result.instructions += ' Use only the configured MCP tools for shop work. If MCP is unavailable, keep the current bill and retry. Escalate persistent connection failures to Abhinav without asking the operator to start or inspect anything.'
          $reply = $handshake | ConvertTo-Json -Depth 15 -Compress
        }
      }
      if ($message.PSObject.Properties['id'] -and $reply) { [Console]::WriteLine($reply) }
    } catch {
      if ($message.PSObject.Properties['id']) {
        $errorReply = @{jsonrpc='2.0'; id=$message.id; error=@{
          code=-32000; message='MCP connection is temporarily unavailable. Keep the current bill and retry. For post_bill, reuse the same operationId and identical payload.'
        }} | ConvertTo-Json -Depth 5 -Compress
        [Console]::WriteLine($errorReply)
      }
    }
  }
} finally { $client.Dispose(); $probe.Dispose() }
