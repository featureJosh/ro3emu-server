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
if [[ -z "${PROTONPATH:-}" ]]; then
  # Prefer a locally installed GE-Proton release so umu-run does not try to
  # download its generic GE-Proton alias when the machine is offline.
  proton_roots=(
    "$HOME/.local/share/Steam/compatibilitytools.d"
    "$HOME/.steam/root/compatibilitytools.d"
    "$HOME/.steam/steam/compatibilitytools.d"
    "/usr/share/steam/compatibilitytools.d"
    "/usr/local/share/steam/compatibilitytools.d"
  )
  proton_candidates=()
  for proton_root in "${proton_roots[@]}"; do
    [[ -d "$proton_root" ]] || continue
    while IFS= read -r proton_dir; do
      proton_candidates+=("$proton_dir")
    done < <(find "$proton_root" -mindepth 1 -maxdepth 1 -type d -name 'GE-Proton*' -print)
  done
  if ((${#proton_candidates[@]})); then
    PROTONPATH="$(printf '%s\n' "${proton_candidates[@]}" | sort -V | tail -n 1)"
  else
    PROTONPATH="GE-Proton"
  fi
fi
export PROTONPATH
export GAMEID="${GAMEID:-umu-ro3emu}"
unset RO3_SHIELD_READY
mkdir -p "$WINEPREFIX"
cd "$RO3_CLIENT_DIR"
exec umu-run ./ro3.exe "$@"
