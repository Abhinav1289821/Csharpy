# Restores fresh demo copies of both firms from the production backups, so the app can be tried without touching ISTMBM26/ISTMP26.
param([string]$Server = "localhost\SQLEXPRESS", [string]$Backups = "C:\MK_WORKSPACE\Context\Databases")
$ErrorActionPreference = "Stop"
foreach ($pair in @(@("MKHQ_DEMO_MBM","ISTMBM26.bak"), @("MKHQ_DEMO_MP","ISTMP26.bak"))) {
  $db = $pair[0]; $bak = Join-Path $Backups $pair[1]
  $dataDir = (sqlcmd -S $Server -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))").Trim()
  $sql = "IF DB_ID('$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END; RESTORE DATABASE [$db] FROM DISK='$bak' WITH MOVE 'ISTDMO12_Data' TO '$dataDir$db.mdf', MOVE 'ISTDMO12_Log' TO '$dataDir${db}_log.ldf', REPLACE, RECOVERY"
  sqlcmd -S $Server -E -C -b -Q $sql | Out-Null
  Write-Host "Restored $db from $bak"
}