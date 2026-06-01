#!/bin/sh
# protostar installer (Linux/macOS).
# Downloads the latest release binary and self-installs it via `protostar install`.
#
#   curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh
#
# Any extra args are forwarded to `protostar install` (e.g. --dir, --no-modify-path) when the
# script is run directly (not via the piped one-liner).
set -e

repo="voidprojectssoftware/protostar"

os=$(uname -s)
arch=$(uname -m)

case "$os" in
  Linux)  rid_os="linux" ;;
  Darwin) rid_os="osx" ;;
  *) echo "Unsupported OS: $os" >&2; exit 1 ;;
esac

case "$arch" in
  x86_64 | amd64)  rid_arch="x64" ;;
  aarch64 | arm64) rid_arch="arm64" ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 1 ;;
esac

asset="protostar-${rid_os}-${rid_arch}"
url="https://github.com/${repo}/releases/latest/download/${asset}"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Downloading ${asset} ..."
curl -fsSL "$url" -o "$tmp/protostar"
chmod +x "$tmp/protostar"
"$tmp/protostar" install "$@"
