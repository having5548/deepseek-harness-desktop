#!/usr/bin/env bash
# Linux（Ubuntu 22.04+ / Debian 12+ / UOS 1070 / deepin 23）构建 .deb 包
# 用法: bash scripts/build-deb.sh
set -euo pipefail
cd "$(dirname "$0")/.."

# 1. 系统依赖检查（构建期需要 dev 头文件，运行期由 deb depends 声明）
MISSING=()
for pkg in libwebkit2gtk-4.1-dev libgtk-3-dev build-essential curl; do
  dpkg -s "$pkg" >/dev/null 2>&1 || MISSING+=("$pkg")
done
if [ ${#MISSING[@]} -gt 0 ]; then
  echo "缺少构建依赖，请先执行："
  echo "  sudo apt update && sudo apt install -y ${MISSING[*]} libssl-dev libayatana-appindicator3-dev librsvg2-dev file"
  exit 1
fi

# 2. 捆绑运行时
bash scripts/prepare-runtime.sh

# 3. 构建 .deb
cargo tauri build --bundles deb

echo
echo "DONE: src-tauri/target/release/bundle/deb/*.deb"
