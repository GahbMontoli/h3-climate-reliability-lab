[CmdletBinding()]
param(
    [ValidateRange(2000, 2100)]
    [int]$Year = 2025,

    [ValidatePattern('^[A-Za-z0-9]{3,10}$')]
    [string]$Station = 'A101'
)

$ProjectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$ProjectFile = Join-Path $ProjectRoot 'src\H3Monitor.Cli\H3Monitor.Cli.csproj'
$OutputDirectory = Join-Path $ProjectRoot "output\inmet-$Year-$($Station.ToUpperInvariant())"

dotnet run --project $ProjectFile -c Release -- `
    --mode inmet `
    --year $Year `
    --station $Station `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "A coleta INMET terminou com o código $LASTEXITCODE."
}
