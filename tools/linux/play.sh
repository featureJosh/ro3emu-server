#!/usr/bin/env bash
# Starts a patched RO3 client under Proton (umu-run). Patch first with:
#   dotnet run --project src/Ro3Emu.ClientTools -- patch "$RO3_CLIENT_DIR" http://127.0.0.1:5380 data/gate_rsa_public.pem
# The patch disables the launcher proof, so ro3.exe can be started directly. RO3_SHIELD_READY stays unset: the client
# then connects to the gates directly instead of through the RO3ShieldHost relay.
# env: RO3_CLIENT_DIR (required), WINEPREFIX (default ~/.local/share/ro3emu/prefix), PROTONPATH (default GE-Proton).
set -euo pipefail
: "${RO3_CLIENT_DIR:?set RO3_CLIENT_DIR to the folder with ro3.exe}"
[[ -f "$RO3_CLIENT_DIR/ro3emu-backup/RO3.ClientSecurity.dll" || ! -f "$RO3_CLIENT_DIR/ro3_Data/Managed/RO3.ClientSecurity.dll" ]] \
  || { echo "client is not patched; run ClientTools patch first" >&2; exit 1; }
export WINEPREFIX="${WINEPREFIX:-$HOME/.local/share/ro3emu/prefix}"
export PROTONPATH="${PROTONPATH:-GE-Proton}"
export GAMEID="${GAMEID:-umu-ro3emu}"
unset RO3_SHIELD_READY
mkdir -p "$WINEPREFIX"
cd "$RO3_CLIENT_DIR"
exec umu-run ./ro3.exe "$@"
