# Starts MK HQ Billing (if it is not already running) and opens it in the browser with its local access key.
param([switch]$NoBrowser, [switch]$Demo)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$url = "http://127.0.0.1:47320"
$exe = Join-Path $root "publish\MK.Hq.Api.exe"
$dataDir = if ($Demo -or -not (Test-Path $exe)) { Join-Path $root "src\MK.Hq.Api\var-dev" } else { Join-Path $root "publish\var" }
$tokenFile = Join-Path $dataDir "api.token"
$mode = if ($Demo -or -not (Test-Path $exe)) { "demo" } else { "live" }

function Test-Up { try { (Invoke-RestMethod "$url/api/health" -TimeoutSec 2).app -eq "mk-hq" } catch { $false } }

# The instance on the port is the one this mode starts only if it accepts this mode's access key: demo and live keep
# separate data folders (var-dev, publish\var), so a live service rejects the demo key and vice versa. Without this,
# "-Demo" while live was running opened the live firms with a key they refuse (or plain start opened the demo copies).
function Test-Ours {
  if (-not (Test-Path $tokenFile)) { return $false }
  $key = (Get-Content $tokenFile -Raw).Trim()
  try { Invoke-RestMethod "$url/api/operations?take=1" -Headers @{ Authorization = "Bearer $key" } -TimeoutSec 5 | Out-Null; $true } catch { $false }
}

if (-not (Test-Up)) {
  if (@(Get-NetTCPConnection -LocalPort 47320 -State Listen -ErrorAction SilentlyContinue).Count) { throw "Port 47320 is used by another program; nothing was started." }
  if ($mode -eq "live") {
    $credentialPath = Join-Path $env:LOCALAPPDATA "MK HQ Billing\sql-credential.xml"
    if (Test-Path -LiteralPath $credentialPath) {
      $credential = New-Object System.Management.Automation.PSCredential("MkHqBilling", (Import-Clixml -LiteralPath $credentialPath))
      $previousUser = $env:Hq__UserId
      $previousPassword = $env:Hq__Password
      try {
        $env:Hq__UserId = $credential.UserName
        $env:Hq__Password = $credential.GetNetworkCredential().Password
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
      } finally {
        $env:Hq__UserId = $previousUser
        $env:Hq__Password = $previousPassword
      }
    } else {
      Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
    }
  } else {
    $dotnet = Join-Path $root ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = "dotnet" }
    $project = Join-Path $root "src\MK.Hq.Api\MK.Hq.Api.csproj"
    Start-Process -FilePath $dotnet -ArgumentList @("run","--project",('"' + $project + '"'),"--environment","Development","--urls",$url) -WorkingDirectory $root -WindowStyle Hidden
  }
  for ($i = 0; $i -lt 120 -and -not (Test-Up); $i++) { Start-Sleep -Milliseconds 500 }
  if (-not (Test-Up)) { throw "MK HQ Billing did not start. Run it in a console to see why." }
}
if (-not (Test-Ours)) {
  $other = if ($mode -eq "demo") { "on the real firms" } else { "on the demo copies" }
  throw "MK HQ Billing is already running $other. Close it (Task Manager: MK.Hq.Api or dotnet) and start again, so the $mode firms open."
}
$token = (Get-Content $tokenFile -Raw).Trim()
Write-Host "MK HQ Billing is ready at $url ($mode)"
if (-not $NoBrowser) { Start-Process "$url/#token=$token" }
