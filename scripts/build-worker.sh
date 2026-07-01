#!/usr/bin/env bash
# Publish the .NET worker as a self-contained single-file exe and place it
# where Tauri expects the sidecar during `tauri build`.
#
# IMPORTANT: Tauri's sidecar resolver (relative_command_path) looks for the
# binary at  <exe_dir>/<externalBin_name>.exe  — it does NOT consult the
# `binaries/` subfolder or the target-triple suffix at runtime. `tauri build`
# copies the suffixed binary from src-tauri/binaries/ to a plain
# `<name>.exe` next to the built exe automatically. This script only needs to
# put the suffixed binary in src-tauri/binaries/ so `tauri build` can find it.
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-dotnet}"
TARGET_TRIPLE="x86_64-pc-windows-msvc"

echo "==> Publishing worker (self-contained single-file)..."
"$DOTNET" publish worker -c Release

SRC="worker/bin/Release/net8.0/win-x64/publish/javideo-worker.exe"
DEST="src-tauri/binaries/javideo-worker-${TARGET_TRIPLE}.exe"

mkdir -p src-tauri/binaries
cp "$SRC" "$DEST"
echo "==> Sidecar placed at $DEST (for tauri build to pick up)"
