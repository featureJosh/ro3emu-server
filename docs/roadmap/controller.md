# Controller roadmap

Goal: the controller is **the** way to play this offline RO3. From launch to logout, nothing needs a mouse or a
keyboard: login, server and character select, character creation, every in-game screen, chat and settings. Every
prompt shows the right button glyph. The controller UI is our own, Diablo-style: compact, readable at 1080p and 4K,
built for a couch distance. It is a remake, not a reskin of the mobile UI.

Status legend as in [ROADMAP.md](../../../ROADMAP.md): `[x]` done and verified in game, `[ ] 🔄` in progress, `[ ]`
not started, `❓` needs investigation. Tick an item only after it works in game.

## How it is built

- **Code:** client hooks (`src/Ro3Emu.ClientHooks`), C# loaded into the client. The signed Lua cannot be changed, so
  the hooks replace Lua table functions with C# delegates (see `LuaPatches`, `Controller`) and build Unity UI from C#.
- **Input:** the game's own Input System actions (`RO3Current`, `RO3_Fixed`; `enum_InputKey`) carry gamepad
  bindings, so game logic sees the same events as from the keyboard. Our own controller actions (cursor, camera,
  navigation, radial menus) are read directly from the gamepad.
- **UI:** one overlay canvas of our own (`ScreenSpaceOverlay`, `CanvasScaler` scale-with-screen from 1920x1080, plus a
  user **UI scale**), drawn above the game's UI. Data comes from the game: the stock widgets keep running hidden
  (alpha 0, no raycasts) and we read their icons, fills and texts, or we call the Lua helpers they use. Where a screen is
  rebuilt, the stock panel stays closed or hidden and our screen calls the same Lua actions.
- **Navigation everywhere:** a focus system that works on any stock panel without per-panel code: it finds the
  clickable elements of the top panel, moves focus spatially with the D-pad / left stick, clicks with ✕, closes with ○,
  scrolls scroll views with the right stick and switches tabs with L1/R1. Rebuilt screens use the same system.
  Per-panel rules (order, skips, default focus) are data, not code.
- **Glyphs:** PS5 set (Kenney Input Prompts, CC0, `Glyphs/`, named after Input System controls). Xbox set to add.
  Glyph style follows the setting, or the connected pad (auto).
- **Dev loop:** `<client>/ro3emu/cmd.txt` (`tree`, `shot`, `panels`, `find`, `dumptex`), hierarchy in
  `ro3emu/tree.txt`, screenshots in `ro3emu/shot.png`. Every screen is checked by screenshot at 1080p and 4K.

## Phase 0: foundation

- [x] Gamepad bindings on the game's actions, live-reloaded `ro3emu/controller.ini` (`08e9a98`).
- [x] Controller mode in Select Mode (relabelled Mobile column) saves Modern PC + controller flag (`810d1ce`,
  `bc00967`; the mobile-UI fallback fixed, re-check after restart).
- [x] PS5 glyph set embedded; dev command file (`9a5bcf4`).
- [x] Controller card uses its own mode value 4; no mobile HUD on re-pick (`79e753e`, verified by the player).
- [x] Glyphs replace stock key labels (`InputBinder`) in controller mode (`79e753e`, verified by the player: shown, but
  too small to read the shapes at the label size).
- [ ] 🔄 `GlyphScale` (default 2.4x, live) plus a 34 px floor: 2.4x was still ~14 px on the skill bar (screenshot).
- [ ] 🔄 HUD inventory: dump the PC HUD widgets (skill bar, HP/SP, buffs, minimap, quest tracker, chat, team, target).
- [ ] 🔄 Overlay canvas + UI scale (`353bc6f`); theme (frame, fonts from the game's TMP assets, colours); glyph component that follows
  bindings and glyph style.
- [x] Login, character select and world entry with the pad only (player, 2026-10-14; `353bc6f`).
- [ ] 🔄 Navigation core: focus scan of the top panel (stock buttons, toggles, sliders, dropdowns, input fields, scroll
  views), spatial moves, focus ring, ✕ click / ○ back / L1 R1 tabs / R-stick scroll, hold-to-repeat.
- [ ] Prompt bar on every screen (✕ Select, ○ Back, △/□ per screen), built from what the focused screen supports.
- [ ] Device detection: controller connected / disconnected, "press any button" wake, keyboard and mouse still work
  side by side; glyphs switch to keyboard when the keyboard is used.
- [ ] Rumble (hit taken, level up, MVP), DualSense lightbar from HP (optional, settings).

## Phase 1: from launch to the world, without a keyboard

- [ ] Login screen: focusable account field, on-screen keyboard (✕ type, □ delete, △ space, L1/R1 move cursor,
  OPTIONS done), remember last account, ✕ to log in. Any account works on our server.
- [ ] On-screen keyboard component, reused by chat, naming, search, mail, stall titles.
- [ ] Server list, notices, agreements and pop-ups: navigable, ✕ / ○.
- [ ] Character select: L/R to change character, ✕ enter, △ create, □ delete with hold-to-confirm.
- [ ] Character creation: job, gender, appearance sliders and lists, name with the on-screen keyboard.
- [ ] First Select Mode on world entry: defaults to Controller when a pad is connected.
- [ ] Loading screens and cut-scenes: skip with ✕ / OPTIONS where the game allows it.

## Phase 2: in the world (Diablo-style HUD)

- [ ] Bottom-centre action bar: HP orb (left), SP orb (right), six skill slots on ✕ ○ □ △ / L1+, R1+ layers with
  icons, cooldowns, charges and SP-cost dimming; potions and shortcut items on the D-pad; EXP bar.
- [ ] Hide the stock skill bar, HP/SP bars and keyboard key labels while Controller mode is on.
- [ ] Skill layers: hold L2 / R2 for a second bar (12+ skills without menus), configurable.
- [ ] Targeting: soft lock on the nearest enemy in the stick direction, R1 cycle, R3 lock, target frame with HP and
  casting bar, ally targeting for heals and buffs (L1 + face button or a toggle).
- [ ] Ground-target skills: aim reticle on the right stick, ✕ confirm, ○ cancel.
- [ ] Analog movement (stick angle and magnitude instead of 8 WASD directions), auto-run toggle.
- [ ] Context prompt: talk to NPC, pick up item, open chest, use portal / Kafra, revive, mount (✕ label changes with
  what is in front of you).
- [ ] Loot: pick up nearest / all, item-name popups with rarity colours.
- [ ] Compact quest tracker (one main quest, objectives, auto-path with a button), minimap with zoom on the D-pad
  layer, buffs/debuffs row, party frames.
- [ ] Radial menus: TOUCHPAD / CREATE opens the menu wheel (bag, equip, skills, character, map, quests, guild, friends,
  mail, shop, settings); hold D-pad for consumables and emotes.
- [ ] Combat feedback: damage numbers sized for 4K, low-HP vignette, rumble.
- [ ] Death / revive panel, level-up and job-change notices.

## Phase 3: menus rebuilt for the controller

Each rebuilt screen: full-screen or side-panel layout, focus navigation, tabs on L1/R1, prompts, sorting / filtering on
△ / □, item comparison on R3, tooltips that follow focus. Order by how often players use them.

- [ ] Inventory and equipment (paper doll, compare, equip / use / drop / split, cards, refine entry points).
- [ ] Skills (tree, learn points, assign to the controller bar by button press, skill plans).
- [ ] Character (stats, allocate points, titles, appearance).
- [ ] NPC dialog and choices (D-pad, ✕; dialog advance on ✕), quest log, quest rewards.
- [ ] World map and teleport (cursor on the map, waypoints, auto-path).
- [ ] Shops, Kafra storage, trade, stalls, mail.
- [ ] Settings, including a **Controller** page: remap every action in game (press to bind, conflict warning,
  reset), stick sensitivity / inversion / dead zones, glyph style (PS5 / Xbox / auto), UI scale, HUD layout, vibration,
  aim assist. Writes `controller.ini`.
- [ ] Chat: channel tabs, quick-chat wheel, on-screen keyboard; read-only mode by default.
- [ ] Party, guild, friends, mail lists.
- [ ] Pets, cards, totems, mounts, wardrobe, collection, achievements, activities, season, rankings: navigation layer
  first, rebuilt by usage.
- [ ] Mini-games (music game, chicken, dungeons with special HUDs): controls mapped and prompts shown.

## Phase 4: polish

- [ ] Everything audited: a full play session (launch, create, tutorial, combat, shop, party with bots, logout) with
  the keyboard and mouse unplugged.
- [ ] 4K and 1080p screenshot pass for every rebuilt screen; ultrawide.
- [ ] Steam Deck / Steam Input check (glyphs, on-screen keyboard vs Steam's).
- [ ] Performance: HUD updates at most once a frame without allocations; no hitches when screens open.
- [ ] Translation: all our strings through the English text layer.

## Known gaps and risks

- ❓ Which components the stock UI uses for buttons (Lua `UIButtonNew` etc.) and whether the focus scan can click them
  through `ExecuteEvents` or needs their Lua `SetOnClick` callbacks. First tree dump answers it.
- ❓ Whether the game's EventSystem module accepts our navigation events (legacy StandaloneInputModule assumed).
- ❓ Text input: TMP input fields focused from C# and filled by our keyboard, also on the login screen (before Lua's
  world modules load).
- Hundreds of stock panels: until rebuilt, each must at least be fully navigable (phase 0 navigation core).
