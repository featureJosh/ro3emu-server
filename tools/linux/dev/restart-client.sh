#!/usr/bin/env bash
# Installs the freshly built hooks DLL, stops the client, starts it again (background), waits for the login panel.
set -u
root="${RO3_ROOT:-$(cd "$(dirname "$(readlink -f "$0")")/../../../.." && pwd)}"; m="$root/RO3 Launcher/Client/ro3_Data/Managed"
L="$HOME/.local/share/ro3emu/prefix/drive_c/users/steamuser/AppData/LocalLow/gravity/ro3/Player.log"
cp "$root/ro3emu-server/src/Ro3Emu.ClientHooks/bin/Debug/netstandard2.1/Ro3Emu.ClientHooks.dll" "$m/.hooks.tmp" && mv -f "$m/.hooks.tmp" "$m/Ro3Emu.ClientHooks.dll"
p=$(pgrep -f '^X:.*ro3\.exe$'); [ -n "$p" ] && kill $p
for i in $(seq 40); do pgrep -f "umu-run ./ro3.exe" >/dev/null || break; sleep 1; done
before=$(date +%s)
(cd "$root" && RO3EMU_FORCE_CONTROLLER=1 setsid nohup ./start-game.sh >/dev/null 2>&1 &)
for i in $(seq 120); do
  if [ -f "$L" ] && [ $(stat -c %Y "$L") -ge $before ] && grep -aq "Ro3Emu.UiNav\] menu: UI_Sys_LoginPanel" "$L"; then echo "login screen after $((i*2))s"; exit 0; fi
  sleep 2
done
echo "login screen not reached"; exit 1
