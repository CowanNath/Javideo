#!/usr/bin/env bash
# Assemble a portable, double-clickable distribution from `tauri build` output.
#
# Layout (what Tauri's sidecar resolver expects at runtime):
#   Javideo-app/
#   ├── javideo.exe            (main app, from target/release/)
#   └── javideo-worker.exe     (.NET sidecar, copied next to the exe)
#
# Run AFTER:  scripts/build-worker.sh  then  scripts/tauri-build.bat --no-bundle
set -euo pipefail
cd "$(dirname "$0")/.."

REL="src-tauri/target/release"
APP="Javideo-app"

if [ ! -f "$REL/javideo.exe" ]; then
  echo "❌ 先运行 tauri-build.bat --no-bundle 生成 javideo.exe"
  exit 1
fi

echo "==> 组装 $APP/ ..."
rm -rf "$APP"
mkdir -p "$APP"

# Main app exe (dist frontend + Rust shell, already embedded by tauri build).
cp "$REL/javideo.exe" "$APP/"

# Sidecar must sit NEXT TO the exe, named <externalBin_name>.exe (no triple,
# no binaries/ subfolder — that's how Tauri's relative_command_path finds it).
# tauri build already copied it here; fall back to the suffixed source copy.
if [ -f "$REL/javideo-worker.exe" ]; then
  cp "$REL/javideo-worker.exe" "$APP/"
else
  cp src-tauri/binaries/javideo-worker-x86_64-pc-windows-msvc.exe "$APP/javideo-worker.exe"
fi

echo "==> 完成。双击 $APP/javideo.exe 即可运行。"
ls -la "$APP/"
