#!/usr/bin/env bash
# Builds the WebAssembly version into dist/web and serves it on http://localhost:8080.
# Needs the wasm-tools workload once: dotnet workload install wasm-tools
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
dotnet publish "$here/PaperReader.Browser.fsproj" -c Release -o "$repo/dist/web"
exec python3 "$here/serve.py" "$repo/dist/web/wwwroot" "${1:-8080}"
