# Running on Linux (server native, client under Proton)

The server, the data export and the client patcher all run natively on Linux. The client is a Windows Unity build
and runs under Proton through `umu-run`.

## Toolchain

| Software | Notes |
|---|---|
| .NET 10 SDK | e.g. `dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet` |
| Node.js 20+, Java 17+ | e.g. `brew install node openjdk` |
| `unluac.jar` | in the repository root |
| `umu-run` + a Proton build | GE-Proton recommended |

## Export, run, patch, play

```bash
export RO3_CLIENT_DIR="/path/to/Client"   # the folder with ro3.exe
dotnet run --project tools/LuaDecode -c Release -- "$RO3_CLIENT_DIR/ro3_Data/StreamingAssets/Recovery/LuaPayload" re/clientB/luac
javac -cp unluac.jar -d tools/unluac-batch tools/unluac-batch/BatchUnluac.java
java -cp "unluac.jar:tools/unluac-batch" BatchUnluac re/clientB/luac re/clientB/lua-src
dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/config
dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/sceneeditor 'Config\SceneEditor'
# Not in the main README, but the server needs both: skill templates (targeting, range, hit timing) and English text
dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/skills 'Config\Skill'
dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/config Localization_en.luac
node tools/WorldData/navexport.js data/nav
node tools/msgtable.js re/clientB/lua-src data/msgtable.tsv

dotnet run --project src/Ro3Emu.Server -c Release          # first start writes data/gate_rsa_public.pem

dotnet build src/Ro3Emu.ClientHooks && dotnet build src/Ro3Emu.Loc
dotnet build src/Ro3Emu.ClientTools --no-incremental
dotnet run --project src/Ro3Emu.ClientTools -- patch "$RO3_CLIENT_DIR" http://127.0.0.1:5380 data/gate_rsa_public.pem
tools/linux/play.sh
```

`play.sh` uses `WINEPREFIX` (default `~/.local/share/ro3emu/prefix`) and `PROTONPATH` (default `GE-Proton`, which
umu downloads if needed; point it at an installed Proton folder to avoid that). Unity's log is in the prefix at
`drive_c/users/steamuser/AppData/LocalLow/gravity/ro3/Player.log`.

## Asia recovery client (RO3 Asia Launcher)

The Asia build adds two assemblies that the patcher handles when present:

- `RO3.AsiaProfile.dll`: `RecoveryRegionProfile.Parse` only accepts the official HTTPS hosts in
  `StreamingAssets/Recovery/region-profile.ini`. The patch makes `RecoveryRegionProfile.Load` return null, so the
  client falls back to the built-in NA URLs, which are rewritten to the emulator along with the notice origins.
  Side effect: the Asia language key (`CurrentLanguage.asia`) and Simplified Chinese default are not used.
- `RO3.ClientSecurity.dll`: `LauncherProofGuard.Enforce` (exit 70 unless RO3AsiaLauncher passes a proof over an
  inherited Win32 pipe) and `SingleInstanceGuard.Enforce` become no-ops, so `ro3.exe` starts directly.

The `shield/` folder (`RO3ShieldHost.exe`, `Shield.dll`) is a local relay: `RO3.GlobalShield.Routing` only maps the
gate ports to `127.208.16.149` when `RO3_SHIELD_READY=1`. Without it the client connects to the gates directly, so
nothing has to be patched; `play.sh` unsets the variable.

Do not run `RO3AsiaLauncher.exe` on a patched client: its repair restores the original DLLs. `ClientTools restore`
undoes the patch.
