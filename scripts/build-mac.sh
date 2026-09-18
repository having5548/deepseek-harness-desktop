#!/usr/bin/env bash
# macOS 构建 .app / .dmg（需在 macOS 上执行；建议 Xcode CLT + Rust + Node）
# 用法: bash scripts/build-mac.sh
set -euo pipefail
cd "$(dirname "$0")/.."

bash scripts/prepare-runtime.sh
cargo tauri build --bundles dmg

echo
echo "DONE: src-tauri/target/release/bundle/dmg/*.dmg（未签名；首次打开需右键 → 打开）"
