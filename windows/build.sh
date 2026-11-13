#!/bin/bash
# build.sh — na Linuxe postaví build/Regula.exe pre Windows (.NET Framework 4.8 je v každom Windows 10 a 11).
#   ./build.sh            → build/Regula.exe
# Treba dotnet SDK (~/.dotnet, inštalácia bez roota: dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet).
# Ikona: Regula.ico z make_icon.py.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}

echo "== kompilácia"
"$DOTNET" build Regula.csproj -c Release -o build -nologo -clp:NoSummary -warnaserror
ls -la build/Regula.exe
sha256sum build/Regula.exe
