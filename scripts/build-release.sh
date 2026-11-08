#!/usr/bin/env bash
# Кросс-сборка Windows-релиза из Linux/macOS.
# Требуется .NET SDK 8 и zip. WPF не запускается, но EXE собирается.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Не найден dotnet. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
  exit 1
fi

if ! command -v zip >/dev/null 2>&1; then
  echo "Не найден zip. Установите пакет zip." >&2
  exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "== Проверка ABI =="
dotnet run --project tests/InteropLayoutChecks/InteropLayoutChecks.csproj -c Release

echo "== Публикация Windows x64 =="
mkdir -p artifacts
rm -rf artifacts/PanelRemote
dotnet publish src/PanelRemote/PanelRemote.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:Platform=x64 \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o artifacts/PanelRemote

EXE="artifacts/PanelRemote/PanelRemote.exe"
if [ ! -f "$EXE" ]; then
  echo "Не найден результат сборки: $EXE" >&2
  exit 1
fi

rm -f artifacts/PanelRemote-win-x64.zip
(cd artifacts/PanelRemote && zip -9 -q ../PanelRemote-win-x64.zip PanelRemote.exe)

echo
echo "Готово: $EXE"
echo "Архив:  artifacts/PanelRemote-win-x64.zip"
sha256sum "$EXE" || true
