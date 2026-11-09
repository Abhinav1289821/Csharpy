# Builds a self-contained Windows x64 release (no .NET install needed on the shop PC) into mk-hq\publish.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $root "src\MK.Hq.Api\MK.Hq.Api.csproj") -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o (Join-Path $root "publish")
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Write-Host "Published to $(Join-Path $root 'publish')"