#!/usr/bin/env bash
set -euo pipefail

echo "[post-create] Restoring NuGet packages..."
dotnet restore

echo "[post-create] Applying EF Core migrations..."
if command -v dotnet-ef >/dev/null 2>&1; then
  dotnet ef database update --project ./twoSaaSCore.csproj
else
  dotnet tool install --global dotnet-ef >/dev/null 2>&1 || true
  export PATH="$PATH:/home/vscode/.dotnet/tools:/root/.dotnet/tools"
  dotnet ef database update --project ./twoSaaSCore.csproj
fi

echo "[post-create] Done. Run with: dotnet run --project ./twoSaaSCore.csproj --urls http://0.0.0.0:5000"
