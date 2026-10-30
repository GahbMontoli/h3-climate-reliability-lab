[CmdletBinding()]
param(
    [ValidateRange(2, 10000)]
    [int]$Replications = 60,

    [ValidateRange(1, 3650)]
    [int]$Days = 30,

    [ValidateRange(1, 1440)]
    [int]$IntervalMinutes = 30,

    [int]$Seed = 20261029
)

$ProjectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$ProjectFile = Join-Path $ProjectRoot 'src\H3Monitor.Cli\H3Monitor.Cli.csproj'
$OutputDirectory = Join-Path $ProjectRoot 'output\research'

dotnet run --project $ProjectFile -c Release -- `
    --mode experiment `
    --replications $Replications `
    --days $Days `
    --interval-minutes $IntervalMinutes `
    --seed $Seed `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "A pesquisa H3 terminou com o código $LASTEXITCODE."
}
