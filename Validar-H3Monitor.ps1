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
    --output $ResearchOutput

if ($LASTEXITCODE -ne 0) {
    throw "O experimento de validação falhou com o código $LASTEXITCODE."
}

Write-Host 'Validação do H3 Monitor concluída com sucesso.' -ForegroundColor Green
