# Offline population

The optional population service fills occupied public maps with ambient player characters. They have names,
Novice/first-job models and male/female appearances, stand around and walk on the exported navigation grid.
They are server actors, with no client connection, account, saved character, inventory or rewards.
This is an original RO3 implementation inspired by the population feature in
[Flux159/ragnarokoffline.app](https://github.com/Flux159/ragnarokoffline.app#filling-the-world); no code was copied.

## Try it

On a server built from `feature/offline-population`, enter a public map and type in chat:

- `!bots 20` — enable up to 20 inhabitants per occupied public map/line.
- `!bots status` — show actual population, per-map target and total limit.
- `!bots 5` — reduce each map to five.
- `!bots off` — remove all inhabitants immediately.
- `!bots reset` — restore the startup configuration.

These commands use the existing GM account policy (`!help` lists the command). Overrides last until restart.
Counts apply server-wide, including maps occupied by LAN friends, rather than only to the command sender.

To enable on every startup, edit `Ro3Emu.Population` in `src/Ro3Emu.Server/appsettings.json` before building:

```json
"Population": {
  "Enabled": true,
  "PerMap": 20,
  "MaxTotal": 100
}
```

Or start the server with configuration arguments (no file edits required):

```bash
dotnet run --project src/Ro3Emu.Server -c Release -- \
  --Ro3Emu:Population:Enabled=true --Ro3Emu:Population:PerMap=20 --Ro3Emu:Population:MaxTotal=100
```

Disabled by default. The per-map target is clamped to 0–100; the total ceiling to 0–500. The total ceiling also
limits GM commands. A zero ceiling disables spawning. Counts are targets: a small or obstructed navigable area
may hold fewer actors. Missing navigation means that map gets none. No external AI service or internet is used.

## Behavior and limits

The service checks once per second. Characters spawn 4–24 map units around real players, roam locally, and are
removed within the next update after the last player leaves. When players travel more than 100 units away,
old characters are reclaimed and replaced near the players. In a shared map, new placement alternates between
players. Public lines have independent populations; private dungeons and event instances receive none. Reconnect
grace still counts as presence until the server removes the real player from World.

The total budget is allocated in scene/line order, so maps later in that order can get fewer characters when
several friends spread out. Bots do not consume account slots or public-line admission capacity. They are WorldUnit
actors and use the server-unit AOI path; they have their own bounded population and do not use the real-player
view-limit/rebalancer. Lower `PerMap` if the client renders too many models.

These are **ambient inhabitants**, not combat companions. They do not fight, heal, accept parties, sell items,
chat, complete quests, or support normal player social interactions. Displayed levels match the spawning player's
level for atmosphere and do not imply functional combat stats. No character data is written. A restart clears
all bots. Combat, companions and interactive shops are future work.

## Protocol evidence and validation

- `src/Ro3Emu.Server/Protos/romsg/MG_Entity.proto`: `EntityBase`, `AoIViewChangeData`.
- `GameMessageHandler.SelfEntity`: existing remote player shape, entity type 3, career template, gender, camp 1,
  HP/SP/level/speed properties and seven equipment slots. PopulationUnit uses the same basic shape.
- Client `re/clientB/lua-src/Manager/LC_UnitManager.lua`, `CreateUnit_Server`: creates non-local units from AOI
  data by UUID. `Network/NW_Helper_PBDefine.lua` maps `SYNC_AOI_VIEW` and `MOVEMENT_CHANGE`.
- Existing `GameAoiSink` uses 31037 to enter/leave and 31081 to move units; PopulationService reuses World and
  these messages. Late viewers receive the current walk segment with remaining duration.
- `Maps/NavGrid.cs`: half-metre segment checks with terrain heights. Both spawning and roaming require a
  walkable straight segment and a nearby height layer. This is bounded local roaming, not full pathfinding.
- Spawn policy, crowd limits, names, job distribution and roaming are **inferred offline-server behavior**;
  they do not reconstruct official RO3 bot behavior. UIDs use the separate 2^50 range, below the client's
  exact double-integer limit of 2^53.

`PopulationTests` uses synthetic config/navigation and the real World visibility path to cover creation,
late joins, disappearance, map/line isolation, global limits, disabled/missing/blocked navigation, runtime
changes, relocation, walking and late-view movement. In-game rendering, selection and LAN behavior still require
manual verification; a passing server test is not proof that all client models render correctly.

Manual acceptance: on a public map run `!bots 20`; check names, male/female job models and walking; invite a LAN
friend and compare inhabitants; move apart, change line and enter/leave a dungeon; run `!bots off` and verify
all ambient actors disappear. Check the server log for errors. Normal combat and quests should remain usable.
