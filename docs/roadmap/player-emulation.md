# Player emulation roadmap

A proposal, not a list of shipped features. It supersedes the companion rules in
[offline-population.md](offline-population.md) wherever the two disagree.

## The rule (owner, 2026-10-24)

**A bot is a player whose client is software.** It is invited, kicked, promoted, rewarded, killed and revived the same
way as a human, through the same server code. The one intended difference: a bot accepts a party invitation by itself.
Any other difference is a gap to close, not a feature.

Test for the rule: game services (`TeamService`, `DungeonService`, `CombatService`, reward settlement, matching) contain
no bot-only branches. Today they contain `IsCompanion` / `PopulationService.IsUid` checks; that count should go to zero.

## Where bots differ today (audit at server `6058888`)

| Area | Today | A player |
|---|---|---|
| Identity | `PopulationUnit` world actor, UID in the 2^50 range, no account, lost on restart | account + saved character |
| Session | no `GameSession` (it needs a `PhonestChannel`), so no packets, bag, progress, tasks or mail | full session |
| Party | invite routed to `InviteCompanionCoreAsync`; leader may only kick it; never vice leader; `MembersOf` (dungeon admission) and `TeamService.Match` count humans only; friend teammate records skip it; roster card is built by `CompanionDetail` | all normal paths |
| Leadership | can inherit leadership since 2026-10-24 but has no session to act as leader | accepts applicants, invites, starts matches |
| Combat | inferred stats (HP 200 + 50 x level), a handful of skills, no SP cost, damage stops at 1 HP (no killing blows), monsters do not target companions, not a friendly-skill target | full rules |
| Rewards | none: no EXP, loot or quest credit; not counted in the party EXP bonus | normal settlement |
| Travel | one map, no portals; teleports to its owner; waits outside private dungeons | portals, Kafra, lines, instances |
| Social and economy | flavor chat only; no friends, whispers, guilds, trade, stalls or auctions | all |

## The architecture decision (owner sign-off needed first)

| Option | How | For | Against |
|---|---|---|---|
| **A. Protocol bots** | Each bot is a real account logged in through `Ro3Emu.Bot` (`BotClient`, the scripted client the tests and load tests already use) over loopback; a brain drives it with client messages only | The server cannot tell a bot from a human, so the rule holds by construction. No change to upstream-shared services, so upstream merges stay clean. `BotClient` already does login, party, combat and chat in tests | Per-bot socket, RSA/RC4/zlib and protobuf cost. The load tests aim at 500 protocol bots within 2 server cores; the cost on this PC is not measured yet |
| **B. In-process sessions** | A loopback `PhonestChannel` lets a `GameSession` live without a socket; the brain reads the session's outbound messages directly | Same fidelity as A, much cheaper per bot | Changes `GameSession` / channel code that upstream also edits, so every sync risks conflicts |
| C. Keep `PopulationUnit` and add more special cases | Today's direction | Cheapest per bot | Breaks the rule: bots stay NPCs that look like players |

**Recommendation:** A first, with B as a later optimization if measurements show A is too heavy. Optionally keep a
**tier 0 crowd**: cheap `PopulationUnit` extras for far-away background density. One that a player interacts with
(invite, trade, whisper, inspect) is **promoted**: a tier 1 protocol bot with the same name and look logs in in its
place. Whether tier 0 stays at all is the owner's call.

## Phases

Each phase ends with its acceptance checks passing on synthetic data, and an in-game check. Order matters: later
phases assume earlier ones.

### Phase 0: spike and decisions
- [ ] Measure the cost of protocol bots on this PC: `Ro3Emu.LoadTest` S2 at 25 / 50 / 100 / 300 bots (tick p99, CPU,
  memory), plus the same counts with today's `PopulationUnit` crowd for comparison.
- [ ] Owner decides: option A, B or a hybrid; whether tier 0 extras stay; bot characters persist across restarts
  (recommended) or are re-rolled; how many bots may be online at once.
- [ ] Decide where the brains run: an in-server hosted service (simplest) or a separate process like `Ro3Emu.BotCli`
  (crash isolation, can run on another PC).

### Phase 1: identity and accounts
- [ ] Bot accounts and characters are created through the normal register, login and character-create paths, with
  career, gender and look from the existing loadout rules (level/area-appropriate gear, no disguised endgame looks).
- [ ] A bot roster with stable identities (name, look, job, personality), saved like any character. This replaces
  the "identity seeds" milestone of the population roadmap.
- [ ] An account flag marks bot accounts for operator tools only (`!bots status`, cleanup). Game rules never read it.
- [ ] Online schedule: bots log in and out over the day like people; one session per account like humans.
- **Acceptance:** a bot appears in the friend search, profile and inspection like any player; a restart brings back
  the same bot with the same character.

### Phase 2: the brain runtime
- [ ] A per-bot behavior loop that acts only through client messages: walk, stop, cast, target, interact with an NPC,
  chat, emote, use item.
- [ ] Port today's behavior to it: roles (explorer, hunter, socializer, crafter, merchant, rester), journeys,
  social groups, chatter, greetings, reactions and gestures. Navigation uses the same exported `NavGrid`.
- [ ] Bounded work per tick (path search, decisions, chat), as today, and a hard cap on bots per map/line.
- **Acceptance:** with tier 0 off, a town looks as lively as today's population, using only real sessions.

### Phase 3: party parity
- [ ] Remove the `TeamService` special cases. Invites go through the normal `NotifyInvitee` and the brain answers
  `IsAgree = true` (the one allowed difference). Kick, leave, leader change and vice leader work exactly as for humans.
- [ ] A bot leader acts: accepts or refuses applicants, invites, hands leadership on, starts a match when asked.
- [ ] Bots count in dungeon admission (`MembersOf`), matching (`TeamService.Match`) and friend teammate records.
- [ ] `!bots follow / stay / assist / passive` become requests the brain obeys (party chat or a hook command), not
  server-side state on a unit.
- **Acceptance:** no `IsCompanion` left in `TeamService`; a human-free party of bots stays together and runs on its own.

### Phase 4: combat parity
- [ ] Real stats from the bot character's level, attributes and equipped gear (the normal stat pipeline).
- [ ] Skills cast through the normal cast path with SP costs, cooldowns and cast times; the full career skill list,
  priorities per role (tank, support, damage), AoE and buff/debuff use.
- [ ] Killing blows allowed; kills settle through the normal reward path: the bot gets its EXP and loot, and counts
  toward the party EXP bonus, exactly as a human member would.
- [ ] Monsters target bots; friendly skills (heals, buffs, revives) target bots, which activates the thanks reactions.
- [ ] Death, revive points and paid revive work as for players.
- **Acceptance:** a party of one human and four bots clears a field boss; rewards settle once per member through the
  normal settlement; nothing in combat code checks for bots.

### Phase 5: progression
- [ ] Bots level through real EXP, spend attribute and skill points, take job changes through the real job quests and
  NPCs, pick talents, and equip what they loot or buy.
- [ ] Gear upkeep: strengthening, refining and cards, within the same rules and costs as players.
- **Acceptance:** a bot started at level 1 reaches its first job change with no GM command.

### Phase 6: travel and instances
- [ ] Portals, Kafra travel, wings and line changes through the real messages; cross-map journeys.
- [ ] Dungeons as normal party members: entry, fights, wipes, retries, leaving. Story and scripted dungeons follow
  the same scene steps as for humans.
- [ ] Queued modes (Survivor Battle, Mock Battle, Monster Hunt, timed events) through the real queues.
- **Acceptance:** a bot party enters a story dungeon, clears it and the instance closes cleanly, with no instance
  code aware of bots.

### Phase 7: quests
- [ ] Bots accept, progress and deliver quests through `TaskService` like players, starting with main story errands,
  then gathering and multi-map chains. Simulated "NPC visits" are retired.
- [ ] A bot's quests never touch another player's quest state (already true when it is a real character).
- **Acceptance:** a bot completes the main story's first chapter on its own.

### Phase 8: social and economy
- [ ] Friends: bots accept or send friend requests (an owner policy), and answer whispers from phrase packs and context.
- [ ] Guilds: bots found, join and leave guilds, take part in guild activities (bonfire, caravan, Ilmata, league).
- [ ] Trade, stalls, auctions and mail through the normal economy services, with an owner-set budget policy so bots
  neither flood nor drain the market. The economy ledger checks cover them like any player.
- **Acceptance:** a bot sells loot at a stall; its money and items reconcile in the economy snapshot.

### Phase 9: scale and operations
- [ ] A population scheduler maps today's density targets (city 50, town 30, field 12, remote 3) to which bots log
  in where and when, within the global cap and CPU budget.
- [ ] Metrics per bot and per brain tick; `!bots` operator commands drive the scheduler.
- [ ] Two-player LAN check: both humans see the same bots doing the same things.

### Phase 10: retire the special cases
- [ ] Remove `PopulationUnit` bot code that phases 1-9 replaced (or keep it as the tier 0 crowd if the owner chose so),
  `TeamService.Companions`, `CombatService.Companions` / `CompanionSupport` / `Population`, and the companion option set.
- [ ] Rewrite [the usage guide](../guide/offline-population.md) for the new model.

## Rules for every phase

- Bots send only what a real client can send; every server check applies to them. A bot that needs something no
  client could do is a design error.
- Bot behavior (when to log in, what to say, how to spend) is offline-server policy, marked `(inferred)` where it is
  implemented, as the repository rules require. The server's game rules stay 1:1.
- Prefer code outside upstream-shared services (brain, scheduler, bot accounts) so upstream merges stay clean.
- Tests run on the synthetic data set; every phase adds a "no bot branch" check for the services it touched.
