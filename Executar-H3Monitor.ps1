[CmdletBinding()]
param(
    [ValidateRange(1, 3650)]
    [int]$Days = 90,

    [ValidateRange(1, 1440)]
    [int]$IntervalMinutes = 10,

    [int]$Seed = 20261029
)

$ProjectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$ProjectFile = Join-Path $ProjectRoot 'src\H3Monitor.Cli\H3Monitor.Cli.csproj'
$OutputDirectory = Join-Path $ProjectRoot 'output\demo'

dotnet run --project $ProjectFile -- `
    --days $Days `
    --interval-minutes $IntervalMinutes `
    --seed $Seed `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "O H3 Monitor terminou com o código $LASTEXITCODE."
}
