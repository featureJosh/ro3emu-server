# Bot improvement roadmap

This is a proposal for future work, not a list of available settings. It builds on the population feature
merged into `linux-asia-offline`. All new milestones below are unimplemented unless marked complete.

The goal is a believable, useful offline MMO population: busy towns, quieter wilderness, lively dungeons,
and companions who make solo play more enjoyable. Normal singleplayer and LAN play should remain responsive.
Everything in the core roadmap works offline, with optional features controlled by the server owner.

## Existing foundation

- [x] Ephemeral player actors with profile-backed names and varied offline handles.
- [x] Public-map/line population, global limits, visibility, cleanup and GM population controls.
- [x] Longer routed journeys, NPC visits and simulated hunting routines.
- [x] Normal attacks, selected offensive job skills, retaliation, First Aid and death/revival.
- [x] Player priority over ambient hunting; ambient bot kills grant no player rewards.

The user confirmed the base works in game. The current profile/loadout increment passed Release builds,
39 focused cases and two real-data/initialization smoke checks. The new appearances need in-game acceptance. LAN behavior and the latest name aesthetics still need acceptance checks. Current settings
are documented in [the usage guide](../guide/offline-population.md). Current bots do not chat, join parties,
complete real quests or populate private dungeons. Those are milestones below.

## Implemented increment: profiles and believable loadouts (2026-10-14)

- [x] City/town/field/remote count presets, exact map overrides and per-map opt-out; defaults 50/30/12/3.
- [x] Configured per-area level bounds and equipment rarity ceilings, independent of human visitor level.
- [x] Fair global allocation across occupied public lines and bounded gradual spawning (eight per pass).
- [x] `!bots on`, per-map count/off/reset controls and profile/level/rarity status readouts.
- [x] Stable compatible randomized hair and real visible job equipment; restrained optional headgear at level 20+.
- [x] Beginner-equipment rule: rarity/required-level/compatibility gates, debug-stat exclusion, no automatic
  premium outfits/wings/glows, and equipped base attributes used in actual combat.

Configuration reload, smooth despawning, activity/chat presets, per-view render budgets, complete fashion/color/face
variation and private-dungeon populations are still pending. Settings and commands are in the
[usage guide](../guide/offline-population.md). Server tests cannot establish client model rendering or LAN behavior.

## Recommended sequence

| Order | Milestone | Main benefit | Depends on |
|---|---|---|---|
| 1 | Per-map population profiles and controls | The world has believable busy and quiet areas | Existing population lifecycle |
| 2 | Randomized appearances, activities, chat and reactions | Bots look distinct and feel present | Profiles; client appearance data and chat/event delivery |
| 3 | Temporary party companions | Solo players gain useful teammates | Bot-aware party roster and skill targeting |
| 4 | Instance-aware dungeon populations | More adventurers in dungeons without corrupting encounters | Companion lifecycle and instance ownership |
| 5 | Stronger combat and support AI | Teammates contribute across more content | Companion roles and reward attribution |
| 6 | Stable identities, appearance and world travel | Familiar characters and less repetitive activity | Population profiles and lifecycle |
| 7 | Genuine bot quest progression | Characters pursue structured objectives | Stable identities, travel and quest isolation |
| 8 | Shops, economy and larger social systems | More useful MMO-style interaction | Identity and durable transaction rules |

## 1. Per-map population profiles — first priority

- [ ] Define presets for cities, towns, ordinary fields, remote areas and eligible dungeons, plus exact map-ID overrides.
- [ ] Give each profile a target count, job/level mix, activity mix, chat frequency and allowed spawn/activity zones.
- [x] Resolve settings in a clear order: map override, area-type preset, server default. Let owners disable any map.
- [ ] Apply public-map targets per occupied line; private-dungeon targets apply per eligible instance, not per viewer.
- [ ] Keep a global ceiling, reserve capacity for recruited companions, and share remaining capacity fairly across
  occupied maps. A busy city must not consume the entire budget and leave a friend's map empty.
- [ ] Add a nearby render budget that respects client detail settings and prioritizes human players and party members.
- [ ] Adjust populations gradually, with spawn/despawn delays and hysteresis. Keep models near useful landmarks
  without blocking portals, doorways, quest NPCs or the player's camera.
- [ ] Expose reloadable settings and operator readouts: actual count, target, profile, budget and missing-nav reasons.
  Future controls should support editing a particular map without changing every occupied map.

Illustrative tuning targets, to be adjusted after performance and visual testing:

| Area | Initial target per active map/line or eligible instance | Typical activity |
|---|---|---|
| Large city | 40–60 | Social groups, shops, crafting stops, purposeful walking |
| Small town | 20–35 | Local errands, resting, arrivals and departures |
| Ordinary field | 8–16 | Hunting, exploration and travel |
| Hidden or remote area | 1–4 | Occasional explorers and small groups |
| Eligible dungeon | 16–24 background adventurers, spread across suitable rooms | Travelling groups and rest stops |

Dungeon targets are a later opt-in capability, not a change to today's public-map-only behavior. Combat
companions are a separate subset constrained by the client's party size; 24 adventurers does not mean a
24-member player party. Empty areas should have no expensive simulation.

**Acceptance:** two LAN players see the same city inhabitants; a remote map stays sparse; separate lines and
instances remain isolated; lowering a target cleans up gradually; the global limit holds under all combinations.

## 2. Randomized appearances, activities, chat and reactions — high-value atmosphere

- [x] **Believable equipment rule (user requirement, baseline implemented):** appearance must reflect the bot's real level, combat
  strength and area difficulty. Beginner areas get beginner gear; high-rarity or legendary-looking equipment
  requires an appropriately strong bot in suitable content. A visiting high-level human must not turn a level-1
  area into a crowd of legendary adventurers. Real equipped gear must contribute to the bot's combat attributes;
  cosmetic overrides must not disguise weak bots as endgame characters.
- [ ] **Randomized appearance presets:** vary hairstyles, supported hair colors, faces where available, outfits,
  visible weapons/equipment, headgear and cosmetics. Use valid exported client appearance IDs and respect
  gender, job, level and model compatibility; avoid arbitrary combinations that fail to render.
- [ ] Weight appearance pools by region and role: plain beginner gear, recognizable class equipment, town fashion,
  and occasional distinctive adventurers. Make rare cosmetics rare and avoid nearby bots sharing identical looks.
- [ ] Choose each bot's appearance once per identity seed and keep it stable during its lifetime, map transitions
  and party recruitment. Persistent identity seeds in milestone 6 will preserve looks across later sessions.
  Pure cosmetic variation must not grant items or stats; functional equipped gear already contributes real base attributes.


- [ ] Assign behavior profiles such as explorer, hunter, socializer, crafter, merchant and resting adventurer.
  Give towns more social roles and fields more hunters, with varied pauses and routes rather than synchronized loops.
- [ ] Add editable, localized phrase packs with personality, context, weights and cooldowns. Include casual
  conversation, location comments and brief party-oriented lines. Use the proper bot sender/profile identity.
- [ ] Support nearby chat first. Respect visibility, mute preferences, per-bot cooldowns and a per-map message budget.
  Avoid repeating a recent phrase or broadcasting the same event once for every spectator.
- [ ] React to actual game events: greet a newcomer, thank a player for healing, congratulate a level-up,
  acknowledge being revived, notice a nearby boss, or comment after a shared fight. Start with greetings and
  observable player events; healing/revival reactions activate as bot-friendly targeting and companion support land.
- [ ] Add waves, sitting, turning to face a speaker and short pauses. Reactions should not interrupt combat
  or movement constantly. Greeting the same player repeatedly should have a long cooldown.
- [ ] Offer quiet/normal/chatty settings and a complete chat-off switch. Dialogue remains optional flavor.

Example phrases: "Anyone heading to Prontera?", "Thanks for the heal!", "Nice hit!", "Taking a break by the fountain."
Context packs should contain many alternatives; these examples should not become four endlessly repeated lines.
A cloud language model is not required. An optional local dialogue model can be explored much later if phrase packs
prove insufficient, while the normal offline mode remains complete.

**Acceptance:** a crowd has varied supported hairstyles/outfits/equipment, and a followed bot keeps its look;
conversation stays varied during a 30-minute session; reactions match real events; a LAN event
produces one response, not one per viewer; muted or disabled chat produces no messages.

## 3. Temporary party companions — most useful gameplay feature

- [ ] Let players recruit compatible bots through an explicit interaction or normal invitation flow, then dismiss
  them easily. Start with a small configurable allowance and always respect the existing client party limit.
- [ ] Represent bots throughout the real party roster, portraits, profiles, HP/SP bars, targeting and invitation
  lifecycle. They should not need fake account logins or persistent player inventories to appear as teammates.
- [ ] Add follow, stay, assist, passive and retreat controls, then focus-target and role assignment. A companion's
  human owner controls it; define leader delegation and LAN ownership so players cannot command each other's bots accidentally.
- [ ] Use loose formations and staggered movement. Handle obstacles, height layers, getting stuck and catching up.
  Follow the owner's actual map/line/instance transition after admission, without joining another party's instance.
- [ ] Provide tank, healer/support and damage roles, with appropriate job selection and clear level scaling.
- [ ] Define death, resurrection, disconnect grace, dismissal and party-leader departure. Clean up memberships,
  targets, timers and actor state together; do not leave ghost party members after a relog or server shutdown.
- [ ] Define rewards before enabling companion kills: use the existing human party's eligibility and durable
  settlement rules, grant rewards once, and never create rewards for the ephemeral bot itself. Preserve the
  current no-player-rewards policy for unrelated ambient bot kills.

**Acceptance:** a player recruits, commands, travels with and dismisses a companion; a LAN friend sees the same
roster and can heal it; owner disconnect/reconnect and death/revival behave predictably; rewards settle once.

## 4. Dungeon populations — more life with correct instance boundaries

- [ ] Add dungeon-specific profiles, room budgets, party-like background groups and rest points. Populate suitable
  explored/active rooms rather than every room at instance creation. Higher density must fit the map's geometry.
- [ ] Keep background adventurers and recruited combat companions as distinct roles. Background groups should
  not kill required encounter actors, start boss scripts, activate switches, trigger cutscenes or advance human quests.
- [ ] Route companion combat through the dungeon's real encounter/death/reward owner. Join/leave, wipes, retries,
  room teardown and pending casts must follow the instance lifecycle instead of public-map cleanup rules.
- [ ] Add entry rules for solo/story content, group dungeons, challenge modes and event instances. Default
  unsupported modes off until their encounter behavior has been verified.

**Acceptance:** two parties in the same dungeon configuration see separate populations; a wipe or instance close
removes all bot state; ambient characters cannot complete encounters; companion kills advance intended objectives once.

## 5. Combat and support intelligence

- [ ] Replace basic inferred stats with map-appropriate levels, believable equipment and job attributes. Remove
  the fixed low-HP hunting cutoff once stronger monsters can be assessed using relative power and encounter policy.
- [ ] Add skill priorities, individual cooldowns, SP costs, resource recovery, AoE checks, buffs, debuffs and interrupts.
- [ ] Improve healing, resurrection, tank threat, ranged positioning, retreat and avoidance of visible hazards.
- [ ] Respect the player's focus target and combat mode. Companions should not drag extra packs into a boss fight;
  ambient hunters should avoid player-claimed targets even while a cast or projectile is in flight.
- [ ] Keep boss/MVP participation separately configurable and gated behind encounter/reward integration.

**Acceptance:** tank/support/damage roles are distinguishable; resource/cooldown limits hold; companions follow
commands under pressure; combat neither duplicates rewards nor steals ambient claims from human players.

## 6. Identity, appearance and travel

- [ ] Persist a small optional identity seed so familiar bots return with the same name, look, job and personality.
  Keep temporary actor IDs and party membership separate from that identity.
- [ ] Expand and allow editing of the handle bank and appearance pools; persist the appearance seeds introduced in milestone 2.
- [ ] Add time-of-day routines, recurring local characters and occasional travelling groups with named destinations.
- [ ] Add cross-map routes using valid portals and line/instance admission. Keep offline simulation lightweight
  and activate detailed behavior only where players can encounter it.

**Acceptance:** returning characters are recognizable; despawn/rejoin creates no duplicate profiles; travelling
bots do not leak private-instance state or consume real-player admission slots.

## 7. Actual quests and 8. economy/social systems — later expansions

- [ ] Give bots an isolated quest journal, supported objective types and explicit progression rules. Start with
  simple NPC-to-field errands, then gathering and multi-map quests. Keep simulated NPC visits clearly separate
  from genuine completed quests, and never mutate a human's quest state to make a bot appear productive.
- [ ] Add finite-stock, useful NPC-like vendors and later real trading through existing transaction/market owners.
  Define restocking, pricing and currency sources/sinks before allowing market interaction.
- [ ] Explore small guilds, recruiting messages, friendships, recurring events and player inspection. Economic
  and social behavior should be opt-in and comprehensible rather than generating unexplained player-state changes.

## Checks required across milestones

- [ ] Keep simulation on the game loop; cap pathfinding, combat and chat work independently.
- [ ] Track bot counts by map/line/instance, roles, path failures, chat volume, CPU and pending lifecycle work.
- [ ] Verify empty-world idling, repeated map changes, reloads, disconnects, shutdown and instance teardown.
- [ ] Use focused tests for configuration precedence, packet identity, party ownership, timer cancellation,
  visibility, reward attribution and chat cooldowns; perform in-game and two-player LAN acceptance separately.
- [ ] Keep defaults conservative, provide a clear off switch, and document implemented options as they ship.

Next increments should refine density controls and appearance pools, then add activity roles and bounded nearby chat/reactions. Party companions should be delivered as a complete roster/follow/ownership lifecycle before
enabling dense private-dungeon combat.
