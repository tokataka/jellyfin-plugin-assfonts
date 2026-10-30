#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
mkdir -p "$PROJECT_DIR/.build-cache/nuget" "$PROJECT_DIR/artifacts"
python3 -m unittest discover -s "$PROJECT_DIR/tests" -p 'test_*.py' -v
docker run --rm --user "$(id -u):$(id -g)" \
  -e DOTNET_CLI_HOME=/tmp/dotnet-home -e NUGET_PACKAGES=/nuget -e DOTNET_NOLOGO=1 \
  -v "$PROJECT_DIR:/src" -v "$PROJECT_DIR/.build-cache/nuget:/nuget" -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b \
  dotnet test tests/AssFonts.Tests.csproj -c Release --nologo
python3 "$PROJECT_DIR/package.py"
