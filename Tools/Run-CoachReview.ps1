param(
    [string]$IbtPath = "TestData\iRacing\mx5 cup_okayama_full_2011-05-13.ibt",
    [string]$ReportPath = "TestData\Reports\coach-review.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
& dotnet run --project SimCoach.Tests --no-build -- --coach-review $IbtPath $ReportPath
exit $LASTEXITCODE
