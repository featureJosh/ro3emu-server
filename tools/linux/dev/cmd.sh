#!/usr/bin/env bash
# Usage: cmd.sh < script   — atomically hands a cmd.txt script to the client.
c="${RO3_CLIENT_DIR:-$(cd "$(dirname "$(readlink -f "$0")")/../../../.." && pwd)/RO3 Launcher/Client}/ro3emu"
cat > "$c/cmd.tmp" && mv -f "$c/cmd.tmp" "$c/cmd.txt"
