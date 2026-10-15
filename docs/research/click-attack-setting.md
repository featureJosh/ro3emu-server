# Click-to-attack setting fallback (2026-10-14)

**Retest: unresolved.** The user reports that skills work but click-to-attack still fails. The live log confirms
the fallback installed, with no fallback-use message. The isolated setting mismatch below is not sufficient
to identify the live blocker. A subsequent diagnostic build now traces the first 30 world clicks through
`TouchRaycastPos`, `HandleColliderBase`, `CheckUnitType`, and `IsInBlackArea`, and logs the first 20 actual
attack-setting reads. It preserves the original click handlers and attack gates.

Restart the client and click a few nearby living enemies in both control schemes. Inspect `Player.log` for
`[Ro3Emu.ClickAttackTrace]` (raycast/collider/interaction/visibility/normal-attack ID) and
`[Ro3Emu.CombatSettings] read` (effective setting and reason). If no raycast entries appear after the
trace-installed message, input is stopping before this Lua path. If the trace reaches the collider handler,
its next gate entries distinguish target eligibility from the setting/normal-attack path. This diagnostic
build is installed locally and embedded in the rebuilt Release ClientTools; another gameplay fix is pending
that runtime evidence.

Clicking a monster can select it without attacking even when the server sends enabled battle settings.
The reconstructed Asia client loads `SettingBaseBattle.eSelectTargetNormalAttack_PC` into
`G_ClientCfg.BattleSetting` and the `BaseBattleSetting` asset, but its setting reader uses a separate flat
asset, `eSelectTargetNormalAttack`. An absent flat asset returns 0, which fails the click handler's `== 1` gate.

Client evidence (decompiled paths under `re/clientB/lua-src/`):

- `Logic/Asset/LC_DAL_Setting.lua`, `_processBaseBattleInfo`: loads the suffixed preference into battle settings.
- `Logic/Setting/LC_SettingProcessFunc.lua`, `ContinuousAttackAfterTargetSelected.ValueSetting.GetValue/SetValue`:
  the getter returns 0 unless ChaseLogic is 1, then reads the flat asset; the setter writes that asset.
- `Logic/Actor/LC_Actor_ControlMove.lua`, `HandleColliderBase`: requires the effective setting to equal 1 before
  dispatching `RJ_onClickSkill` for `CurrentNormalAttackSkillId`.
- `Config/Define/ClientConfig.lua`, `getBattleSetting`: resolves the current PC/mobile suffix.

`CombatSettings.Install`, called once per Lua environment from `LuaPatches.Tick`, replaces only this custom
getter through the existing xLua table API. It first calls the original getter and preserves explicit 1/2
values. If that getter has no valid preference, it retains the ChaseLogic gate and reads the loaded preference
through `G_ClientCfg.getBattleSetting`. It resolves each call, so subsequent preference/platform changes are
observed. It does not force-enable attack or write/save preferences. A client missing the expected functions
keeps its original getter and logs a warning.

Validation:

- Release ClientHooks build against the installed Asia client succeeded without warnings.
- Release ClientTools build succeeded, including the updated hooks as an embedded resource; existing Loc
  `netstandard` reference warning remains.
- Release server build succeeded; existing nullable warning in `GuideService.cs` remains.
- Ten focused regression cases passed by directly invoking the compiled xUnit tests in-process:
  enabled login preference, explicit on/off priority, chase modes 2/3, updated platform preference/player edits,
  missing/invalid loaded preference, and incompatible client. Tests link the production `CombatSettings.cs`
  against minimal xLua/Unity boundary stubs, so they do not prove the real xLua bridge or combat in game.
- Normal `dotnet test` execution (hooks/patcher/server suites) was blocked by sandbox socket/IPC permissions.
  No full-suite pass is claimed. The direct runner is `/tmp/ro3-combat-check/Program.cs`.
- A direct in-process attempt at the patcher suite stopped on its first update test: the sandbox's root mount
  reports 0 MB free to `DriveInfo`, tripping the updater's 200 MB space guard. No patcher-suite pass is claimed.

Run the focused tests outside that restricted environment:

```bash
dotnet test tests/Ro3Emu.ClientHooks.Tests -c Release
```

The built hooks were installed into the local client's `ro3_Data/Managed/Ro3Emu.ClientHooks.dll`, with the
previous DLL saved in a dated `ro3emu-backup/before-combat-settings-*` directory. ClientTools was rebuilt so
future Release patching also carries the fix. Restart the game to load the replacement DLL.

## Project-manager acceptance check

1. Restart the client, log into the existing character, and check `Player.log` for
   `[Ro3Emu.CombatSettings] click-to-attack setting fallback installed`.
2. Click a nearby living enemy in classic controls, then action controls. Confirm an attack animation, damage,
   and server skill traffic. On first fallback use, expect `missing flat attack setting; using loaded battle
   preference 1`. If neither fallback nor attack occurs, inspect the earlier target-eligibility/click gates.
3. Disable click-to-attack explicitly in settings: clicking should select without attacking. Re-enable it and
   confirm attacking resumes. Confirm the preference survives logout/login.
4. Set ChaseLogic away from 1: the fallback must not override that gate. Restore it after checking.

In-game acceptance and real xLua callback execution remain unverified. This fix addresses the diagnosed setting
mismatch; it does not establish that every subsequent combat gate or server combat behavior works.

## Root cause found in game (2026-10-15, Claude)

The fallback above was installed but never used: the flat setting was not missing, it was an explicit **2 (off)**.
`ClickProbe` (enable with `RO3EMU_CLICK_PROBE=1`) logged every enemy click reaching `HandleColliderBase` with
`ContinuousAttack=2 ChaseLogic=1 flat=2 CurrentNormalAttackSkillId=1000001`.

- `ContinuousAttackAfterTargetSelected` is setting id **403001** (`Config/Define/Lua_GameDefine.lua:17193`), saved
  by the client as `DBSettingInfo.clientSetting[403001]` (a message, field 1 = value) and restored at login.
- Its only writer in this build is the first-time mode pick (`UI_Sys_SetModeWidgetCtrl`, when
  `pcPlatformRecord` is false): 1 for classic, **2 for action**. There is no settings toggle for it. Role 100001
  picked action mode, so every later login restored "off" in both control schemes.
- Fix (`GameMessageHandler.ForceClickToAttack`): the server stores and sends 403001 = 1 when settings load, and when
  the client saves another value it corrects it and sends `AutoSyncSettings` back at once. Players could click-attack
  in both modes on the official servers (player report), so this is inferred original behaviour.
- Verified in game: `ContinuousAttack=1 flat=1` → `RJ_onClickSkill dispatched: iSkillId=1000001 bOnlyClick=True` →
  server `casts skill 1000001` → `killed … (5000001)`.
- `CombatSettings` stays as a harmless fallback for a role that never had the value. ClientHooks tests: 10/10 pass.
