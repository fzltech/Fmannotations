#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet build "$SCRIPT_DIR/src/Fmverify.Annotations.Analyzers/Fmverify.Annotations.Analyzers.csproj" -c Release
dotnet pack "$SCRIPT_DIR/src/Fmverify.Annotations/Fmverify.Annotations.csproj" -c Release -o "$SCRIPT_DIR/.packages"
