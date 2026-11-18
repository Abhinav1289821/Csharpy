# Restores fresh demo copies of both firms from the production backups, so the app can be tried without touching ISTMBM26/ISTMP26.
param([string]$Server = "localhost\SQLEXPRESS", [string]$Backups = "C:\MK_WORKSPACE\Context\Databases")
$ErrorActionPreference = "Stop"
foreach ($pair in @(@("MKHQ_DEMO_MBM","ISTMBM26.bak"), @("MKHQ_DEMO_MP","ISTMP26.bak"))) {
  $db = $pair[0]; $bak = Join-Path $Backups $pair[1]
  if (-not (Test-Path $bak)) { throw "Backup not found: $bak" }
  $dataDir = (sqlcmd -S $Server -E -C -b -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))").Trim()
  if ($LASTEXITCODE -ne 0 -or -not $dataDir) { throw "Could not reach SQL Server $Server" }
  # RESTORE ... REPLACE overwrites in place, so a failed restore never leaves the demo database dropped.
  $sql = "IF DB_ID('$db') IS NOT NULL ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$db] FROM DISK='$bak' WITH MOVE 'ISTDMO12_Data' TO '$dataDir$db.mdf', MOVE 'ISTDMO12_Log' TO '$dataDir${db}_log.ldf', REPLACE, RECOVERY; ALTER DATABASE [$db] SET MULTI_USER"
  # $ErrorActionPreference does not cover native programs in Windows PowerShell: check sqlcmd's exit code ourselves.
  sqlcmd -S $Server -E -C -b -Q $sql
  if ($LASTEXITCODE -ne 0) { throw "Restoring $db from $bak failed (sqlcmd exit code $LASTEXITCODE)" }
  Write-Host "Restored $db from $bak"
}