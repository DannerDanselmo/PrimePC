# Gera a versão distribuível do PrimePC: um único PrimePC.exe autocontido (não exige .NET instalado).
param([string]$Saida = "$PSScriptRoot\publicado")

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }

Write-Host "Executando testes automatizados..."
& $dotnet test "$PSScriptRoot\tests\PrimePC.Tests" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Os testes falharam; publicação cancelada." }

Write-Host "Publicando em $Saida ..."
& $dotnet publish "$PSScriptRoot\src\PrimePC.App" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $Saida --nologo
if ($LASTEXITCODE -ne 0) { throw "Falha na publicação." }

Copy-Item "$PSScriptRoot\README.md" "$Saida\LEIA-ME.md" -Force
Write-Host "Pronto: $Saida\PrimePC.exe"
