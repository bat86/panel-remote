# Сборка «Пульта доски» для Windows 10/11 x64.
# Запуск: powershell -ExecutionPolicy Bypass -File .\build.ps1
# Готовит два варианта:
#   1) один self-contained EXE (удобно для школы, но антивирусы иногда ругаются
#      на self-extracting упаковку — это ложное срабатывание);
#   2) папку с файлами (без single-file и без самораспаковки) — для случаев,
#      когда антивирус удаляет одиночный EXE.
param(
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\PanelRemote\PanelRemote.csproj"
$output = Join-Path $root "artifacts\PanelRemote"
$zip = Join-Path $root "artifacts\PanelRemote-win-x64.zip"
$folderOutput = Join-Path $root "artifacts\PanelRemote-folder"
$folderZip = Join-Path $root "artifacts\PanelRemote-win-x64-folder.zip"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден .NET SDK 8. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

New-Item -ItemType Directory -Force -Path (Join-Path $root "artifacts") | Out-Null

Write-Host "Проверка ABI..." -ForegroundColor Cyan
dotnet run --project (Join-Path $root "tests\InteropLayoutChecks\InteropLayoutChecks.csproj") -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Проверка размеров нативных структур не прошла."
}

Write-Host "Сборка одного EXE: $project" -ForegroundColor Cyan

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с кодом $LASTEXITCODE"
}

$exe = Join-Path $output "PanelRemote.exe"
if (-not (Test-Path $exe)) {
    throw "Не найден результат сборки: $exe"
}

$size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor DarkGray

if (-not $NoZip) {
    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }

    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Архив: $zip" -ForegroundColor Green
}

Write-Host ""
Write-Host "Сборка папкой (без single-file): $project" -ForegroundColor Cyan

if (Test-Path $folderOutput) {
    Remove-Item $folderOutput -Recurse -Force
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $folderOutput

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (папка) завершился с кодом $LASTEXITCODE"
}

$folderExe = Join-Path $folderOutput "PanelRemote.exe"
if (-not (Test-Path $folderExe)) {
    throw "Не найден результат сборки папкой: $folderExe"
}

$folderSize = [Math]::Round(((Get-ChildItem $folderOutput -Recurse | Measure-Object Length -Sum).Sum) / 1MB, 1)
$folderHash = (Get-FileHash -Algorithm SHA256 $folderExe).Hash.ToLowerInvariant()
Write-Host "Готово: $folderOutput ($folderSize МБ)" -ForegroundColor Green
Write-Host "SHA-256 PanelRemote.exe: $folderHash" -ForegroundColor DarkGray

if (-not $NoZip) {
    if (Test-Path $folderZip) {
        Remove-Item $folderZip -Force
    }

    Compress-Archive -Path (Join-Path $folderOutput "*") -DestinationPath $folderZip -CompressionLevel Optimal
    Write-Host "Архив: $folderZip" -ForegroundColor Green
}
