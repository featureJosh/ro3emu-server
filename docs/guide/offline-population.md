# Offline population

The optional population service fills occupied public maps with simulated player characters. They have names,
Novice/first-job models and male/female appearances, travel between activity spots, hunt ordinary monsters
and use job skills on the exported navigation grid.
They are server actors, with no client connection, account, saved character, inventory or rewards.
This is an original RO3 implementation inspired by the population feature in
[Flux159/ragnarokoffline.app](https://github.com/Flux159/ragnarokoffline.app#filling-the-world); no code was copied.

## Try it

On a server built from `feature/offline-population`, enter a public map and type in chat:

- `!bots 20` — enable up to 20 simulated players per occupied public map/line.
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

The simulation advances every 250 ms. Characters initially spawn 4–24 map units around real players. They
plan journeys to nearby NPCs or monster spawn points (25–140 units away), with 35–100-unit exploration routes
as a fallback. Routes use bounded A* on the exported grid to detour around walls and are simplified into
continuous walking segments of at most 12 units at 5 units/second. A bot pauses at activity destinations;
it does not pause after every segment. Route planning is limited to one bot per pass and 3,000 nodes per search.
Failed paths are retried later. Navigation is local to one map; bots do not cross portals.

The activity loop simulates questing: visit an NPC, travel to hunting grounds, fight a few monsters, return
and pause. **This does not accept or complete real quests**, alter quest NPC state or award quest rewards.
No dialogue windows are opened for the human player. Names come from a shuffled bank of short aliases, nicknames with numbers, gamer-style handles and word pairs
(e.g. Mochi, Ren42, xKurox, SleepyPoring, LuckyMage). Every bank entry is unique ignoring case;
serial numbers draw without replacement, with a separate rollover suffix after exhausting the bank.
No username API or internet access is needed. The word lists and implementation are original.
Online research considered [Markov username generation](https://github.com/karrni/username-generator) and
[configurable username templates](https://github.com/subhamg/unique-username-generator); neither project is a dependency. UserDataService supplies the bot's
Name/Level/Base/Online fields, as the client obtains player head names from that cache rather than EntityBase.Name.

Bots use each job's configured normal attack and a small selection of level-appropriate, hostile single-target
skills from SkillTreeConfig, SkillConfig and visual target selectors. Low-health bots can use Novice First Aid.
The normal combat damage formula and 31100/31101 skill packets are reused. Ordinary, unclaimed public spawn-group
monsters near the bot (within five levels above it and below 10,000 HP) can retaliate; bots can die and revive after 30 seconds. Attribute progression is inferred
(HP 200+50×level, attack/magic attack 12+4×level). This is a small combat simulation, not full character builds:
AoE, buffs/debuffs, equipment bonuses, SP/cost accounting and complex skill scripts are not implemented for bots.

Real players take priority: bots skip damaged, player-targeted or reward-reserved monsters and other bots' targets.
A delayed bot hit checks for intervening player damage and yields rather than finishing that hit. Field MVPs
and independent/single spawns are excluded. Bot kills use normal corpse/respawn handling but grant no player
EXP, loot or quest credit, and never invoke player reward settlement callbacks.

Bots disappear when their public map/line becomes empty. Actors more than 220 units from every real player
are reclaimed and replaced near the players. Placement alternates between players sharing a map. Private dungeons
and event instances receive none. Reconnect grace counts as presence until the player leaves World.

The total budget is allocated in scene/line order, so maps later in that order can get fewer characters when
several friends spread out. Bots do not consume account slots or public-line admission capacity. They are WorldUnit
actors and use the server-unit AOI path; they have their own bounded population and do not use the real-player
view-limit/rebalancer. Lower `PerMap` if the client renders too many models.

Bots have no accounts or saved progression. They do not accept parties, sell items, chat or support the full
player social/inspection workflow. Their profile explicitly disables party invitations. Displayed levels match
the spawning player's level and are simulated progression, not saved characters. A restart clears all bots.
Companions, shops, full quest chains and advanced skill behavior remain future work.
## Protocol evidence and validation

- `src/Ro3Emu.Server/Protos/romsg/MG_Entity.proto`: `EntityBase`, `AoIViewChangeData`.
- `GameMessageHandler.SelfEntity`: existing remote player shape, entity type 3, career template, gender, camp 1,
  HP/SP/level/speed properties and seven equipment slots. PopulationUnit uses the same basic shape.
- Client `re/clientB/lua-src/Manager/LC_UnitManager.lua`, `CreateUnit_Server`: creates non-local units from AOI
  data by UUID. `Network/NW_Helper_PBDefine.lua` maps `SYNC_AOI_VIEW` and `MOVEMENT_CHANGE`.
- Existing `GameAoiSink` uses 31037 to enter/leave and 31081 to move units; PopulationService reuses World and
  these messages. Late viewers receive the current walk segment with remaining duration.
- `Maps/NavGrid.cs`: half-metre segment checks with terrain heights. Both spawning and roaming require a
  walkable straight segment and a nearby height layer. PopulationNavigation adds bounded A* and validates each walking segment.
- Spawn policy, crowd limits, names, job distribution and roaming are **inferred offline-server behavior**;
  they do not reconstruct official RO3 bot behavior. UIDs use the separate 2^50 range, below the client's
  exact double-integer limit of 2^53.

`PopulationTests` uses synthetic config/navigation and the real World visibility path to cover lifecycle,
line isolation, limits, names and real UserDataService responses, long journeys, obstacle detours, delayed hits,
monster retaliation, First Aid and the absence of player kill rewards. In-game names/skill animation and LAN
behavior still require manual verification after each update; a passing server test is not proof of client rendering.

Manual acceptance: restart the main server when convenient, reconnect the regular client and use `!bots 20`; check head names,
follow a bot across a longer journey, observe attacks and job skills near Porings, then attack its target and
confirm the bot yields. Check First Aid/monster retaliation and death/revival. Change line, visit a dungeon,
and use `!bots off` to verify cleanup. NPC stops should look like questing without changing your own quests.

## Planned improvements

See the [bot improvement roadmap](../roadmap/offline-population.md) for per-map density, contextual chat/reactions,
temporary party companions, dungeon populations and later AI/quest/economy work. All items there are proposals
unless marked complete; they are not additional settings available in this version.
