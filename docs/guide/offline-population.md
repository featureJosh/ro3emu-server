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
- `!bots status` — show total population/limit and the current map's profile, target, level range and rarity cap,
  plus a role-mix line and a density line (actual / target / allocated, pending despawns, and why a map has none).
- `!bots stats` — totals per map/line (eight shown), pending despawns, path failures, chat lines in the last minute,
  last pass time and settings reloads.
- `!bots chat off/quiet/normal/chatty` — set the server-wide chatter cadence.
- `!bots mute` / `!bots unmute` — hide/restore bot chatter for your current login session.
- `!bots off` — remove all inhabitants immediately, including maps with runtime overrides.
- `!bots reset` — restore startup settings and clear all runtime map overrides.
- `!bots recruit`, `!bots dismiss [all]`, `!bots follow|stay|assist|passive` — party companions, when enabled (see below).

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
`Ro3Emu:Population` changes in the server's **output-folder** `appsettings.json` (for example
`src/Ro3Emu.Server/bin/Release/net10.0/appsettings.json`) apply without a restart when `ReloadOnChange` is true
(the default). A missing section or malformed value is rejected and the current settings stay; runtime `!bots`
overrides keep precedence until `!bots reset`. A reload replaces `Maps`/`Profiles` with exactly what the file says.

Or enable profiles with configuration arguments:

```bash
dotnet run --project src/Ro3Emu.Server -c Release -- \
  --Ro3Emu:Population:Enabled=true --Ro3Emu:Population:UseMapProfiles=true --Ro3Emu:Population:MaxTotal=100
```

Disabled by default. Per-map targets clamp to 0–100 and the total ceiling to 0–500; a zero ceiling disables
spawning. At most eight new actors spawn per simulation pass by default. Smaller targets receive a fair share
of the global budget before busy maps fill the remaining space. Automatic reductions (fair share moving to a newly
occupied line, a reloaded lower target or a larger `CompanionReserve`) are gradual: a map/line must stay over budget for
`DespawnDelaySeconds` (10, 0–600; 0 = immediate), then at most `MaxDespawnsPerTick` (2, 1–100) leave per pass, out-of-sight
and idle bots first, and that map's refill waits one more delay. `!bots N`, `!bots map N/off`, `!bots off`, empty or
disabled maps, a lowered `MaxTotal` and shutdown still remove at once. `CompanionReserve` (0, 0–100) is kept free under
`MaxTotal`, so ambient bots use at most `MaxTotal − CompanionReserve`.
`LandmarkClearance` (4 map units, 0–30; 0 = off) keeps spawns and pause destinations away from NPC, portal and Kafra
stand points, portal/Kafra landing points and born/revive points; NPC visits stop just outside it, facing the NPC.
Per-viewer render budgets are not implemented: they would need the client's detail settings. Small/obstructed areas can hold fewer actors; missing navigation means none.
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
With `Population:Appearance:Enabled` (default true) each bot rolls one style from its seed: **plain** (hair and job
gear), **town fashion** or rare **distinctive**. Weights are per preset under
`Population:Appearance:Profiles:<city|town|field|remote>:{Plain,Fashion,Distinctive}` (defaults 35/58/7, 45/50/5,
80/18/2, 65/30/5; unknown presets use the field weights). Fashion slots are clothing, face accessory, mouth and foot.
Only default-unlocked, non-luxury, effect-free, sex/model-compatible wardrobe rows are used unless
`UnlockableFashion=true` adds non-luxury collectible fashion. Wings, mounts, glows and luxury rows are never used.
Fashion rarity is capped by the area, the bot's worn gear rarity and common (3); distinctive looks prefer higher rarity
within those caps. A bot avoids the hair/face-accessory/outfit/weapon combination of another bot within
`DistinctRadius` (30) on the same map/line, re-rolling up to `DistinctRetries` (4, max 8) seed variants; a re-roll can
also pick different level-appropriate gear. Cosmetic slots never change stats. Hair color cannot vary: neither the
protocol nor `WardRobeConfig` has a color or dye channel. Exported data may have few default-unlocked fashion rows,
in which case crowds differ mainly by hair, weapon and headgear. Persistent identity seeds remain planned.

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

Bots have no accounts or saved progression. They do not sell items or support the full player social/inspection
workflow. Their profile disables party invitations unless party companions are enabled (below). Levels and gear are
simulated progression based on area difficulty, not saved characters. A restart clears all bots.
Shops, full quest chains and advanced skill behavior remain future work.
## Nearby chatter

Enabled bots use **quiet** chatter by default. Messages use the bot's real profile UID over the existing
Tencent map-chat tunnel (`77011`, notify-text command 4). Recipients must currently see the bot, be within
30 map units, share its exact runtime map/line, be fully loaded, and have joined a map-chat channel.
World/guild/private channels receive none. Both legacy `4_…` and prefixed `@TGS#_4_…` spellings are supported;
a viewer subscribed to both receives only one push. Delivery rechecks scene generation and membership.
Existing blacklist entries and `!bots mute` suppress delivery. Bots do not yet support the normal durable
player-to-player block/friend workflow; use `!bots mute` for the supported personal opt-out. These lines have no replay history, so someone
joining later or far away will not receive old proximity-only conversation.

A new occupied line waits 20–50 seconds before its first possible line. Quiet mode allows at most one line
per map every 45–67.5 seconds and per bot every 180–270 seconds. Normal mode uses 20–30 / 90–135 seconds;
chatty uses 8–12 / 45–67.5 seconds. Each pass plans at most two lines across the server and one per map.
Cooldowns, no nearby eligible listener, combat, death or removal can make actual chatter less frequent.
The previous eight lines per map are excluded. Small custom packs become quiet when exhausted rather than
immediately repeating. Removed/empty population maps release their chatter state; shutting bots off clears it.

Original offline phrase packs cover travel, NPC stops, town pauses, hunting and resting. This is flavor,
not conversation with a language model or evidence that a bot completed a quest. Nearby greetings and observed
base-level gains also produce bounded reactions (below). Shared fights, nearby bosses and support from players also produce reactions (below).
Text does not interrupt movement or combat.

Configure packs under `Ro3Emu.Population.Chat` before restart:

```json
"Chat": {
  "Mode": "quiet",
  "GreetingsEnabled": true,
  "LevelUpReactionsEnabled": true,
  "GreetingCooldownSeconds": 600,
  "Phrases": {
    "travel": ["Taking the scenic route.", "Onward!"]
  }
}
```

Available keys are `travel`, `npc`, `town`, `hunt`, `rest`, `greeting`, `levelup`, `thanks`, `revived`, `sharedfight`,
`boss`, and the role packs `explorer`, `socializer`, `crafter`, `merchant`.
Reaction packs can use `{player}` and `{level}` placeholders; names are reduced to plain text. Use at least nine distinct lines per context for
sustained variation. Each configured context replaces its built-in pack; omitted contexts use the English
defaults. Blank text, control characters and text over 180 characters are ignored.
Unknown modes fail quietly with no bot messages. `!bots chat …` changes the mode until restart/reset;
`!bots reset` restores configured mode and population settings, while personal mute lasts until logout.
All these commands follow the existing GM account policy. The client's own channel display settings still apply.

### Greetings and observed level gains

With reactions enabled (the default), a bot within 12 units can greet a newly encountered nearby player by name.
One shared observation per player prevents every bot in a crowd from greeting them. A greeting can repeat only
after the player leaves and returns and the default ten-minute player cooldown expires; this cooldown follows
the player across public maps/lines while the population remains enabled. `GreetingCooldownSeconds` clamps to
60–3600. Welcome lines share the normal per-bot, map and server budgets, including initial map staggering.

Bots also notice base-level increases in the authoritative `GameSession.Progress` snapshot while a player remains
continuously nearby and eligible to hear chat. Initial login, a return from elsewhere or unmuting establishes a
baseline rather than replaying previous gains. GM level increases also count; this is observation of level state,
not proof of a particular reward source. One bot can congratulate the player, at most once per two minutes.
Repeated gains are coalesced rather than queued individually.

Reactions wait at least five seconds, expire after 90 seconds and are dropped when the subject leaves audible
proximity, becomes muted/ineligible, or moves between map/line generations. A bot in combat or on its own chat
cooldown may miss the opportunity. Player-event flavor is therefore best effort, not guaranteed acknowledgment.
Messages are normal nearby chat: other eligible viewers around the same bot see the same response once.
Delivery rechecks the subject's role ID, generation, eligibility and 12-unit range before each send.

Use `GreetingsEnabled=false` or `LevelUpReactionsEnabled=false` to disable individual reaction types in startup
configuration. `!bots chat off` and personal `!bots mute` also suppress reactions. No human quest, EXP, party or
combat state is changed. Short-lived observations store IDs/levels rather than player-session references, retain
at most 2,048 visitors and expire after the greeting cooldown plus two minutes away; turning the population off clears
them. Saved identities and durable per-player preferences remain future work.

### Shared fights, bosses and support

These reuse the greeting/level-up budgets, delays, mute and chat-off rules. Each event produces at most one line from
one bot, however many bots or LAN viewers are nearby. Switches under `Ro3Emu.Population.Chat`, all `true` by default:

- `SharedFightReactionsEnabled` (`sharedfight`, accepts `{player}`): a bot and a nearby player both damaged a monster
  that died within 20 units of the bot. One comment 2 s after the kill, expiring after 30 s, at most once per player
  every two minutes.
- `BossReactionsEnabled` (`boss`, no placeholder): a living NPCConfig subtype 3/4 monster within 30 units of a bot.
  One line per boss spawn per map/line; maps are scanned every 5 s. Which subtype is field boss versus MVP is inferred.
- `HealReactionsEnabled` (`thanks`) and `ReviveReactionsEnabled` (`revived`): thanks for a player's heal/buff or
  revive. **Inactive today:** friendly skills can only target players, so nothing triggers them until bots become
  valid friendly-skill targets.

### Behavior roles

Each bot gets one stable role at spawn: explorer, hunter, socializer, crafter (stands beside gathering points, never
collects), merchant (idles near Kafra/function NPCs facing passers-by, never trades) or rester. Cities and towns favour
socializers, merchants and resters; fields favour hunters and explorers; remote maps mostly explorers. Set
`Population:Profiles:<area>:Roles` (for example `{"hunter": 5, "explorer": 2}`) to replace an area's mix; unknown keys
are ignored and an all-zero mix uses the defaults. Roles choose destinations, hunting willingness and randomized pauses
(merchants and resters stand longest), so bots drift out of step. Town socializers form groups of 2 to
`Population:Roles:ClusterSize` (3, 2–4) on a small ring, facing the centre, at most eight groups per map. Socializers
chat more often and hunters less, within the existing chat budgets. `Population:Roles:Enabled=false` restores the
original journey loop for newly spawned bots; `SocialClusters=false` turns off groups only.

### Gestures

`Ro3Emu.Population.Gestures`: `Enabled` (true), `FaceSpeakers` (true), `WaveEmojiId` (50001, inferred),
`WaveChancePercent` (50), `SitEmojiId` (50002), `SitChancePercent` (35); an ID of 0 disables that gesture. A bot turns
toward a player it greets and may wave; up to two idle bots within 12 units turn toward a player who speaks in map chat
(once per speaker every 10 s) and pause about 3 s; resting bots may sit and stand up before walking on. Gestures never
start while a bot moves, routes or fights, and end within one pass if that changes. Emote IDs missing from
`PerformanceConfig`, the wrong sex, or maps marked `_iPerformanceForbidden` are skipped. Gestures are world state every
viewer sees; `!bots mute` affects chat only. Wave ID 50001 is a guess that needs an in-game check.

## Party companions (optional)

On by default (companions need visible bots: turn the population on with `Population:Enabled` or `!bots on`). Turn them off
with `"Companions": { "Enabled": false }`. Settings: `"Population": { "Companions": { "Enabled": true, "MaxPerPlayer": 1, "RecruitRange": 20,
"DisconnectGraceSeconds": 60 } }` (`MaxPerPlayer` 0–4, so the party never exceeds five).

- `!bots recruit`: the nearest ambient bot within `RecruitRange` joins your party (one is spawned beside you if none is
  near); a party is created with you as leader if you have none. Inviting a visible bot through the normal party
  invite (click the bot, Team Up; no need to be in a party first)
  also works; it accepts at once, greets in party chat, and a second invite beyond `MaxPerPlayer` is refused (party full).
  With companions off the invite fails with a feature-unavailable error and no party is created.
- `!bots follow` / `!bots stay`: follow with a loose offset, or hold position. `!bots assist` (default) /
  `!bots passive`: help against monsters fighting you, or never attack.
- `!bots dismiss` / `!bots dismiss all`: remove your companion(s). `!bots off` removes companions as well.

Only the owner commands a companion; party friends see it in the roster and the world. The leader can kick it; it
never leads, and leadership always passes to a human. It teleports beside you beyond 40 units or after three failed
paths, reappears beside you after map/line changes, and waits outside private dungeons (shown as away). Dungeon and
match admission count humans only; a merged match party drops companions. If you disconnect, the companion disappears
and leaves the party after the grace period; leaving the party or the last human leaving dismisses it at once. It
revives 30 s after dying. Companions never land killing blows (their damage stops at 1 HP), so your own kill grants
the normal reward once; bots never receive rewards and do not count toward the party EXP bonus. Monsters do not target
companions yet. Companions are not capped by `MaxTotal`; set `CompanionReserve` to keep ambient bots plus companions under it.

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
restrictions, debug exclusions, two-handed weapons and stable appearance packets. Chatter tests cover
arrival delays, cooldowns, editable packs, repetition, map/server budgets, visible nearby recipients, line isolation,
blacklists, personal mute, channel subscriptions, combat/death and cleanup without history replay. Reaction
checks cover shared crowd responses, map-spanning greeting cooldowns, authoritative level gains, suppressed/offline
baselines, delayed/expired events, subject generation changes, plain-text placeholders and lifecycle cleanup. In-game names/skill animation and LAN
behavior still require manual verification after each update; a passing server test is not proof of client rendering.

Manual acceptance: restart the main server when convenient, reconnect the regular client and use `!bots on`; compare city/field density, try `!bots map 3`, and check head names,
follow a bot across a longer journey, observe attacks and job skills near Porings, then attack its target and
confirm the bot yields. Check First Aid/monster retaliation and death/revival. Visit a beginner area with a high-level character: bots
should remain modest Novices with plain gear. Follow one bot and verify its hairstyle/weapon remain stable;
compare its world model with its profile. Change line, visit a dungeon,
and use `!bots off` to verify cleanup. Enable Nearby/map chat, wait up to a minute for quiet chatter,
verify the sender name matches a visible bot and try `!bots mute`/`!bots unmute` and `!bots chat off`.
Two LAN players near the same bot should see the same line once; other lines and distant viewers should not.
Stand within 12 units until one named welcome appears, then leave/return and verify no immediate repeated welcome.
Gain a base level while nearby (a GM level increase can exercise this too); expect at most one congratulations
within the normal chat budget, and confirm off/mute suppresses it. NPC stops should look like questing without changing your own quests.

## Planned improvements

See the [bot improvement roadmap](../roadmap/offline-population.md) for remaining density/appearance refinements, richer chat/event reactions,
temporary party companions, dungeon populations and later AI/quest/economy work. All items there are proposals
unless marked complete; they are not additional settings available in this version.
