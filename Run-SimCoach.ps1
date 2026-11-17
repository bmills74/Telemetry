$ErrorActionPreference = 'Stop'
$simCoachDotnet = Join-Path $PSScriptRoot '.dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $simCoachDotnet)) {
    $simCoachDotnet = 'dotnet'
}
& $simCoachDotnet run --project (Join-Path $PSScriptRoot 'SimCoach.App/SimCoach.App.csproj')
exit $LASTEXITCODE
