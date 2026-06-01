#!/usr/bin/env bash
# Dev runner: build-and-run the protostar CLI in place.
#   ./pstar.sh <args>   ==   protostar <args>
#
# Examples:
#   ./pstar.sh --help
#   ./pstar.sh install-hooks --yes --dry-run
#
# Safe manual testing of hook/install commands: point the harness at a throwaway scratch dir so you
# never edit your real ~/.claude. The CLI honors PROTOSTAR_HARNESS_ROOT for every harness path:
#   export PROTOSTAR_HARNESS_ROOT="$PWD/.dev/harness"
#   ./pstar.sh install-hooks --yes
#   cat .dev/harness/settings.json
set -euo pipefail
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$dir/src/Protostar.Cli" -v quiet -- "$@"
