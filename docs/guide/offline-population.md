# Offline population

The optional population service fills occupied public maps with simulated player characters. They have names,
Novice/first-job models, randomized compatible hair and level-appropriate equipment, travel between activity spots, hunt ordinary monsters
and use job skills on the exported navigation grid.
They are server actors, with no client connection, account, saved character, inventory or rewards.
This is an original RO3 implementation inspired by the population feature in
[Flux159/ragnarokoffline.app](https://github.com/Flux159/ragnarokoffline.app#filling-the-world); no code was copied.

## Try it

On the current main server, enter a public map and type in chat:

- `!bots on` — enable configured profiles: cities 50, towns 30, fields 12, explicit remote profiles 3.
- `!bots 20` — use a uniform target of 20 per occupied public map/line.
- `!bots map 3` / `!bots map off` — override the current map ID across its public lines.
- `!bots map reset` — remove the current map's runtime override.
- `!bots status` — show total population/limit and the current map's profile, target, level range and rarity cap.
- `!bots off` — remove all inhabitants immediately, including maps with runtime overrides.
- `!bots reset` — restore startup settings and clear all runtime map overrides.

Commands use the existing GM account policy (`!help` lists the command). Runtime changes last until restart;
`!bots map` does not enable a disabled master switch. `!bots on` removes the uniform override but preserves
runtime map overrides. An explicitly disabled map in configuration stays disabled under every command.
Counts apply server-wide, including maps occupied by LAN friends.

To enable on every startup, edit `Ro3Emu.Population` in `src/Ro3Emu.Server/appsettings.json` before building:

```json
"Population": {
  "Enabled": true,
  "UseMapProfiles": true,
  "PerMap": 20,
  "MaxTotal": 100,
  "MaxSpawnsPerTick": 8,
  "DefaultAreaLevel": 5,
  "Profiles": {
    "city": { "Count": 50 },
    "town": { "Count": 30 },
    "field": { "Count": 12 },
    "remote": { "Count": 3 }
  },
  "Maps": {
    "10111": { "Count": 8, "AreaLevel": 1, "MaxEquipmentQuality": 2 },
    "10112": { "Profile": "remote" }
  }
}
```

The map IDs above illustrate overrides; choose IDs and difficulty for your own export. Profiles also accept
`MinLevel`, `MaxLevel` and `MaxEquipmentQuality`. Map overrides accept those fields plus `Enabled`, `Profile`,
`Count` and `AreaLevel`. Missing preset fields retain their defaults. A city world-map icon selects `city`;
other maps explicitly marked scene type 0 select `town`; the rest select `field`. Remote areas require an
explicit profile override. These are inferred policies, not official RO3 classifications.

Count precedence is runtime map override, runtime uniform override, configured map count, then profile count
(or `PerMap` when `UseMapProfiles=false`). Level/rarity settings resolve map override before profile defaults.
Changing file settings requires a server restart; live file reload is future work.

Or enable profiles with configuration arguments:

```bash
dotnet run --project src/Ro3Emu.Server -c Release -- \
  --Ro3Emu:Population:Enabled=true --Ro3Emu:Population:UseMapProfiles=true --Ro3Emu:Population:MaxTotal=100
```

Disabled by default. Per-map targets clamp to 0–100 and the total ceiling to 0–500; a zero ceiling disables
spawning. At most eight new actors spawn per simulation pass by default. Smaller targets receive a fair share
of the global budget before busy maps fill the remaining space. Reductions and shutdown remove actors immediately;
gradual despawning remains planned. Small/obstructed areas can hold fewer actors; missing navigation means none.
No external AI service or internet is used.

## Levels, appearances and real equipment

Bot levels come from **the area, never the visiting player's level**. Difficulty uses an explicit `AreaLevel`,
otherwise the median level of unique ordinary monster types in exported spawn groups; maps without this data
use `DefaultAreaLevel` (5). Bots roll within two levels of that difficulty, constrained by configured bounds.
`MinLevel` cannot push a bot above the area's upper bound. Below level 10 they stay Novices; higher areas can
use Novice/first-job careers. These progression rules are inferred and configurable area overrides are useful
where exported spawns do not represent intended difficulty.

Equipment must satisfy its actual required level, gender, career and series restrictions. Maximum quality
is 2 below level 20, 3 at 20+, 4 at 40+, 5 at 60+, and 6 at 80+, also capped by the map/profile setting.
Candidates favor useful nearby item levels and lower rarities. Debug ID ranges and implausibly powerful base
attributes are excluded even if an item claims beginner level/rarity. Two-handed weapons exclude an offhand.
Selected equipment's base attributes and supported primary-stat conversions contribute to actual HP/SP,
attack, magic attack, defense, hit, critical and flee values. These are simulated equipped items, not accounts,
inventories or items granted to humans. Missing equipment data falls back to baseline attributes/default looks.

Each actor receives a stable seeded look for its lifetime: compatible default-unlocked hair, visible job gear
and optional non-luxury headgear from level 20. Headgear rarity cannot exceed the bot's allowed and worn rarity,
and effect-bearing headgear is excluded. Automatic premium outfits, wings, mounts and glow effects are excluded;
beginner bots therefore cannot masquerade as legendary adventurers. AOI and profile responses use the same look.
Hair colors, supported faces, regional fashion pools and persistent identity seeds remain planned.

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
AoE, buffs/debuffs, refinement/cards/enchants, SP/cost accounting and complex skill scripts are not implemented for bots.
The baseline attributes above now receive the selected equipment bonuses.

Real players take priority: bots skip damaged, player-targeted or reward-reserved monsters and other bots' targets.
A delayed bot hit checks for intervening player damage and yields rather than finishing that hit. Field MVPs
and independent/single spawns are excluded. Bot kills use normal corpse/respawn handling but grant no player
EXP, loot or quest credit, and never invoke player reward settlement callbacks.

Bots disappear when their public map/line becomes empty. Actors more than 220 units from every real player
are reclaimed and replaced near the players. Placement alternates between players sharing a map. Private dungeons
and event instances receive none. Reconnect grace counts as presence until the player leaves World.

The total budget is shared round-robin across occupied scenes/lines, up to each target.
At exceptionally small total limits some maps still cannot receive an actor. Bots do not consume account slots or public-line admission capacity. They are WorldUnit
actors and use the server-unit AOI path; they have their own bounded population and do not use the real-player
view-limit/rebalancer. Lower profile/map counts or use `!bots 5` if the client renders too many models.

Bots have no accounts or saved progression. They do not accept parties, sell items, chat or support the full
player social/inspection workflow. Their profile explicitly disables party invitations. Levels and gear are
simulated progression based on area difficulty, not saved characters. A restart clears all bots.
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
monster retaliation, First Aid and the absence of player kill rewards. Further checks cover configuration
precedence/binding, fair budgets, gradual spawning, area-derived levels, real equipment bonuses, rarity/level/job/sex
restrictions, debug exclusions, two-handed weapons and stable appearance packets. In-game names/skill animation and LAN
behavior still require manual verification after each update; a passing server test is not proof of client rendering.

Manual acceptance: restart the main server when convenient, reconnect the regular client and use `!bots on`; compare city/field density, try `!bots map 3`, and check head names,
follow a bot across a longer journey, observe attacks and job skills near Porings, then attack its target and
confirm the bot yields. Check First Aid/monster retaliation and death/revival. Visit a beginner area with a high-level character: bots
should remain modest Novices with plain gear. Follow one bot and verify its hairstyle/weapon remain stable;
compare its world model with its profile. Change line, visit a dungeon,
and use `!bots off` to verify cleanup. NPC stops should look like questing without changing your own quests.

## Planned improvements

See the [bot improvement roadmap](../roadmap/offline-population.md) for remaining density/appearance refinements, contextual chat/reactions,
temporary party companions, dungeon populations and later AI/quest/economy work. All items there are proposals
unless marked complete; they are not additional settings available in this version.
