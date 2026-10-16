# Controller art handoff

The controller HUD, wheel, prompts and keyboard can use PNGs from `<RO3 client>/ro3emu/skin/`. The hook checks the
directory once a second while Controller mode is on. Save a PNG with the exact name below; replacing or deleting it
updates the UI without rebuilding the hook. A missing image uses the current built-in shape. The client art directory is
local and should not be committed.

Draw at 2× the listed 1080p sizes for a crisp 4K presentation. Keep transparent margins tight, use straight alpha,
and export in sRGB. A supplied image is shown in its own colours; the fallback shapes retain their current tints.
Images currently scale to fit their rectangles. Keep borders thick enough to survive scaling; configurable 9-slice
borders are planned but not implemented yet.

| PNG name | 1080p target size | Use |
| --- | ---: | --- |
| `orb_frame` | 150 × 150 | HP and SP outer rings |
| `orb_glass` | 150 × 150 | Shine over both orbs |
| `orb_fill_hp`, `orb_fill_sp` | 138 × 138 | Vertically filled HP and SP liquids |
| `plate` | variable width × 122 | Background behind the action row |
| `plate_edge` | variable width × 2 | Edge above the action row |
| `slot_frame` | 78 × 78 | Skill slot border |
| `slot_cooldown_mask`, `slot_ready_flash` | 78 × 78 | Cooldown sweep and ready flash |
| `potion_frame` | 58 × 58 | Item slot border |
| `wheel_disc`, `wheel_ring` | 760 × 760 | Menu backing and outer ring |
| `wheel_slice`, `wheel_slice_selected` | 156 × 54 | Regular and selected menu entries |
| `prompt_bar` | variable width × 38 | Navigation hint background |
| `interact_prompt` | 230 × 56 | Context action background |
| `settings_panel` | 840 × 750 | Controller bindings page background |
| `osk_key`, `osk_key_focused` | 64 × 64 | On-screen keyboard keys |
| `glyph_buttonSouth`, `glyph_buttonEast`, `glyph_buttonWest`, `glyph_buttonNorth` | 48 × 48 | Optional replacements for the embedded button glyphs |

Other glyph names follow the Input System control name: `glyph_leftShoulder`, `glyph_rightShoulder`,
`glyph_leftTrigger`, `glyph_rightTrigger`, `glyph_leftStick`, `glyph_rightStick`, `glyph_leftStickPress`,
`glyph_rightStickPress`, `glyph_start`, `glyph_select`, and `glyph_dpad-up` (likewise down, left, right).

Use [controller-art-template.svg](controller-art-template.svg) as a layout reference. It is a guide for drawing separate
PNG files, not an atlas loaded by the client. Slot press feedback, cooldown-ready flash, low-HP orb pulse and the wheel
selection animation work with replacement art. The cooldown sweep and HP/SP fill still come from the game's live UI data.

Custom focus ring, 9-slice setup, and font replacement need separate hook work.
