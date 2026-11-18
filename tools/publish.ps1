# Builds a self-contained Windows x64 release (no .NET install needed on the shop PC) into mk-hq\publish.
param([switch]$Staging)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $(if ($Staging) { "artifacts\publish" } else { "publish" })
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = "dotnet" }
$exe = Join-Path $out "MK.Hq.Api.exe"
if (Get-Process -Name "MK.Hq.Api" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
  throw "MK HQ Billing is running from $out; close it first (its files are in use)."
}
# publish\appsettings.json is the shop's installation settings (firm databases, next year's ISTMBM27, server name). A
# re-publish copies the project's file over it; keep the installed one and say so when the two differ.
$settings = Join-Path $out "appsettings.json"
$kept = if (Test-Path $settings) { [IO.File]::ReadAllBytes($settings) } else { $null }
& $dotnet publish (Join-Path $root "src\MK.Hq.Api\MK.Hq.Api.csproj") -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
$failed = $LASTEXITCODE -ne 0
if ($null -ne $kept) {
  $fresh = if (Test-Path $settings) { [IO.File]::ReadAllBytes($settings) } else { $null }
  if ($null -eq $fresh -or [Convert]::ToBase64String($fresh) -ne [Convert]::ToBase64String($kept)) {
    [IO.File]::WriteAllBytes($settings, $kept)
    Write-Warning "Kept the installed publish\appsettings.json; it differs from src\MK.Hq.Api\appsettings.json. Merge any new settings by hand."
  }
}
if ($failed) { throw "Publish failed" }
Write-Host "Published to $out"
