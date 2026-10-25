# Gera o instalador do PrimePC: roda os testes, publica o executável e compila o Setup com o Inno Setup 6.
# Resultado: instalador\PrimePC-Setup-<versão>.exe
$ErrorActionPreference = "Stop"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup" }

# Publica numa pasta própria do instalador, para não conflitar com um PrimePC aberto na pasta "publicado".
$build = "$PSScriptRoot\installer\build"
& "$PSScriptRoot\publicar.ps1" -Saida $build
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar." }

$version = (Select-Xml -Path "$PSScriptRoot\Directory.Build.props" -XPath "//Version").Node.InnerText
Write-Host "Compilando o instalador da versão $version..."
& $iscc "/DAppVersion=$version" "/DSourceDir=$build" "$PSScriptRoot\installer\PrimePC.iss"
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o instalador." }

Write-Host "Pronto: $PSScriptRoot\instalador\PrimePC-Setup-$version.exe"
