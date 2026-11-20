#!/bin/sh
# protostar installer (Linux/macOS).
# Downloads the latest release binary and self-installs it via `protostar install`.
#
#   curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar-cli/main/scripts/install.sh | sh
#
# Pass --channel stable|edge (default stable) as the first argument to pick the release track:
#   stable = the latest tagged release; edge = the rolling prerelease built from the tip of main.
# Any other args are forwarded to `protostar install` (e.g. --dir, --no-modify-path) when the
# script is run directly (not via the piped one-liner).
set -e

repo="voidprojectssoftware/protostar-cli"

# Channel selector (first arg). Everything else is forwarded to `protostar install`.
channel="stable"
case "$1" in
  --channel) channel="$2"; shift 2 ;;
  --channel=*) channel="${1#*=}"; shift ;;
esac

os=$(uname -s)
arch=$(uname -m)

# A shell running under Rosetta on Apple silicon reports x86_64. Ask the kernel for the real CPU so
# the native arm64 binary is chosen.
if [ "$os" = "Darwin" ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null)" = "1" ]; then
  arch="arm64"
fi

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
case "$channel" in
  stable) url="https://github.com/${repo}/releases/latest/download/${asset}" ;;
  edge)   url="https://github.com/${repo}/releases/download/edge/${asset}" ;;
  *) echo "Unknown channel: $channel (use 'stable' or 'edge')" >&2; exit 1 ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Downloading ${asset} ..."
if ! curl -fsSL "$url" -o "$tmp/protostar"; then
  echo "Could not download ${asset} from the ${channel} release: ${url}" >&2
  echo "Release binaries exist for linux-x64, osx-arm64, win-x64 and win-arm64." >&2
  exit 1
fi
chmod +x "$tmp/protostar"
"$tmp/protostar" install "$@"
