[CmdletBinding()]
param()

$ProjectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$SolutionFile = Join-Path $ProjectRoot 'H3Monitor.slnx'
$TestProject = Join-Path $ProjectRoot 'tests\H3Monitor.Core.SmokeTests\H3Monitor.Core.SmokeTests.csproj'
$CliProject = Join-Path $ProjectRoot 'src\H3Monitor.Cli\H3Monitor.Cli.csproj'
$ValidationOutput = Join-Path $ProjectRoot 'output\validation'
$ResearchOutput = Join-Path $ProjectRoot 'output\validation-research'

dotnet build $SolutionFile -c Release -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "A compilação falhou com o código $LASTEXITCODE."
}

dotnet run --project $TestProject -c Release --no-build
if ($LASTEXITCODE -ne 0) {
    throw "Os testes falharam com o código $LASTEXITCODE."
}

dotnet run --project $CliProject -c Release --no-build -- `
    --days 7 `
    --interval-minutes 10 `
    --seed 20261029 `
    --run-id h3-validation-simulation-20261029 `
    --exported-at-utc 2026-10-31T12:00:00Z `
    --output $ValidationOutput

if ($LASTEXITCODE -ne 0) {
    throw "A simulação de validação falhou com o código $LASTEXITCODE."
}

dotnet run --project $CliProject -c Release --no-build -- `
    --mode experiment `
    --replications 12 `
    --days 14 `
    --interval-minutes 30 `
    --seed 20261029 `
    --run-id h3-validation-experiment-20261029 `
    --exported-at-utc 2026-10-31T12:05:00Z `
    --output $ResearchOutput

if ($LASTEXITCODE -ne 0) {
    throw "O experimento de validação falhou com o código $LASTEXITCODE."
}

Write-Host 'Validação do H3 Monitor concluída com sucesso.' -ForegroundColor Green
