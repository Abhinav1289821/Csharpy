# Starts MK HQ Billing (if it is not already running) and opens it in the browser with its local access key.
param([switch]$NoBrowser, [switch]$Demo)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$url = "http://127.0.0.1:47320"
$exe = Join-Path $root "publish\MK.Hq.Api.exe"
$dataDir = if ($Demo -or -not (Test-Path $exe)) { Join-Path $root "src\MK.Hq.Api\var-dev" } else { Join-Path $root "publish\var" }

function Test-Up { try { (Invoke-RestMethod "$url/api/health" -TimeoutSec 2).app -eq "mk-hq" } catch { $false } }

if (-not (Test-Up)) {
  if (@(Get-NetTCPConnection -LocalPort 47320 -State Listen -ErrorAction SilentlyContinue).Count) { throw "Port 47320 is used by another program; nothing was started." }
  if ((Test-Path $exe) -and -not $Demo) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
  } else {
    Start-Process -FilePath dotnet -ArgumentList @("run","--project",(Join-Path $root "src\MK.Hq.Api"),"--environment","Development","--urls",$url) -WorkingDirectory $root -WindowStyle Hidden
  }
  for ($i = 0; $i -lt 120 -and -not (Test-Up); $i++) { Start-Sleep -Milliseconds 500 }
  if (-not (Test-Up)) { throw "MK HQ Billing did not start. Run it in a console to see why." }
}
$token = (Get-Content (Join-Path $dataDir "api.token") -Raw).Trim()
Write-Host "MK HQ Billing is ready at $url"
if (-not $NoBrowser) { Start-Process "$url/#token=$token" }