#!/usr/bin/env bash
# 准备捆绑运行时（Linux/macOS，Rust/Tauri 版）：
# 下载官方 Node 发行包 → 提取 node + npm → 安装 pnpm@11 到 src-tauri/resources/runtime。
# dsh 不捆绑，由应用首次启动时自动安装。
#
# 用法: bash scripts/prepare-runtime.sh
# 环境变量:
#   NODE_VERSION   Node 版本（默认 22.20.0，LTS）
#   NODE_MIRROR    下载镜像前缀（默认 npmmirror 国内加速；可设 https://nodejs.org/dist）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
RT="$ROOT_DIR/src-tauri/resources/runtime"
LOG_FILE="$ROOT_DIR/prepare-runtime.log"

log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$LOG_FILE"; }

NODE_VERSION="${NODE_VERSION:-22.20.0}"
NODE_MIRROR="${NODE_MIRROR:-https://registry.npmmirror.com/-/binary/node}"

mkdir -p "$RT"
echo "prepare-runtime start" > "$LOG_FILE"

# 1. 下载并解压 Node 发行包（含 npm）
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS-$ARCH" in
  Linux-x86_64)  NODE_DIST="linux-x64" ;;
  Linux-aarch64) NODE_DIST="linux-arm64" ;;
  Linux-armv7l)  NODE_DIST="linux-armv7l" ;;
  Darwin-arm64)  NODE_DIST="darwin-arm64" ;;
  Darwin-x86_64) NODE_DIST="darwin-x64" ;;
  *) log "unsupported platform: $OS-$ARCH"; exit 1 ;;
esac

TARBALL="node-v${NODE_VERSION}-${NODE_DIST}.tar.xz"
URL="$NODE_MIRROR/v${NODE_VERSION}/$TARBALL"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if [ ! -x "$RT/node" ]; then
  log "downloading $URL ..."
  curl -fL --retry 3 -o "$TMP/$TARBALL" "$URL"
  log "extracting node + npm ..."
  tar -xJf "$TMP/$TARBALL" -C "$TMP"
  cp "$TMP/node-v${NODE_VERSION}-${NODE_DIST}/bin/node" "$RT/node"
  chmod +x "$RT/node"
  rm -rf "$RT/node_modules/npm"
  cp -R "$TMP/node-v${NODE_VERSION}-${NODE_DIST}/lib/node_modules/npm" "$RT/node_modules/npm"
  log "node $( "$RT/node" --version ) bundled"
else
  log "node already present, skip download"
fi

# 2. 安装 pnpm@11 到捆绑运行时（供 dsh plugin 使用；pnpm 12+ 体积暴涨，不用）
#    注意 unix 下 npm -g --prefix 的 shim 在 $RT/bin/ 下
rm -rf "$RT/node_modules/pnpm" "$RT/bin/pnpm" "$RT/bin/pnpx"
log "npm install pnpm@11 -> runtime ..."
"$RT/node" "$RT/node_modules/npm/bin/npm-cli.js" install -g --prefix "$RT" --no-audit --no-fund pnpm@11 >/dev/null
log "pnpm $( "$RT/node" "$RT/node_modules/pnpm/bin/pnpm.cjs" --version ) bundled"

# 3. 验证
[ -x "$RT/node" ] || { log "node missing!"; exit 1; }
[ -f "$RT/node_modules/npm/bin/npm-cli.js" ] || { log "npm missing!"; exit 1; }
log "runtime ready at $RT"
echo "DONE"
