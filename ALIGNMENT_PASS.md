# Alignment Pass — Working Notes

Target feel: two reference clips (ground-lunge slide attack, aerial dive, camera
roll on displacement only, wall-to-wall dash, heavy weapon identity, toxic-skull
AoE, 3-tier enemy UI, floating damage numerals).

All code compiles (`dotnet build Assembly-CSharp{,-Editor}.csproj`, 0/0).
Play verification pending — values below are first-pass tuning, not final.

## Status

| # | Feature | State | Files |
|---|---------|-------|-------|
| 1 | Slide → dash-attack cancel | in, needs playtest | `SlideController` (0.08–0.7s window), `AttackController` (input pre-check), `dashStreakFx`=`FX_Wind_Streaks_01` |
| 2 | Aerial dive (Genshin-style plunge) | in, needs playtest | `AttackController.DiveAttack`, states `DiveAttack`=`CLazy@WpJAttack_AirDrop_Keep` (loop) + `DiveLand`=`..._End`, `diveImpactFx`=`FX_Impact_Dirt_01`. Near-vertical drop — `diveForwardSpeed` is only a 1 m/s aim-assist toward the lock, not glide momentum |
| 3 | Camera-roll audit | done | tilt raisers = Slide, WallRun/WallDash, jump, LandingRoll; dive = pitch-dip only; attacks/arts raise none |
| 4 | Wall-dash burst | in, needs playtest | `WallRunController` burst branch (jump press + 2nd `WallRunSurface` within `wallDashGap`), `!wallDashing` hijack guards in `LateUpdate`/`OnAnimatorMove` |
| 5 | Weapon pickup | in | `WeaponPickup` stand (E / gamepad-north, swaps both ways), placed by `EnsureWeaponPickup` |
| 6 | Toxic Chorus (Art9) | in | `M_katana_Blade@Skill_I`, 360°/4.2m, `FX_ToxicSkulls` prefab (`SkullBurst` ring + `FX_Poison_Green_01`), `WeaponArt.damageKind`/`bloomPunch`, `PostPulse` lazy Bloom volume |
| 6b | Big-sword skill arts (Art10–23) | in | ALL 14 `M_Big_Sword@Skill_*` takes — A Grave Rend · B Ashen Cleave · C Red Reaver · D Stonebreaker · E Tide Splitter · F Iron Gale · G_1/G_2/G_ALL Grave Wolf chain · H Molten Arc · I Skyfall Edge · J/J_Inplace Mooncleaver (Rush/stationary) · K Bonesunder · L Last Eclipse. `bake`; icons reuse the 3 sword silhouettes; windows are first-guess — tune after watching |
| 7a | Damage numerals | in | `DamageNumberSpawner` pooled pixel digits on `GameHud.worldLayer` |
| 7b | Elite plate | in | `EnemyAI.eliteName` → `GameHud.ShowPlate`; slim bar, collider-top + 0.18 gap |
| 7c | Boss bar top-of-frame | in | `GameHud.BuildBoss` anchored (0.5,1) |
| 8 | Enemy theme | OPEN — skeletons vs crow-swarm/palette-swap; not touched |
| 9 | Art-summoned sword | in | `WeaponSocket.ManifestArtSword` — spectral gold big sword for every art cast |

## Damage numerals — how it works + knobs

- `DamageNumberSpawner.Hit(victim, amount, kind)` from `Health.TakeDamage`
  (backstabs pass `DamageKind.Crit`; poison arts pass `Poison`).
- Spawn point: `collider.bounds.max.y + 0.12` — above the head, never the face.
- Digits: `HudArt.Digit(n, outlined:true)` sprites sized `rect.size × HudArt.T`,
  pitched `0.82 × sprite width` — multi-digit numbers read as one word.
- Scale: `(0.95 + dmg/75)` clamped ≤2.0, ±10% jitter, ×1.25 crit; quantized to
  half-steps so point-filtered texels stay even (fractional scale = blur).
- Motion: pop 0.3→1.3 overshoot settle, rise 90px/s, heavy drop 250px/s²,
  lateral ±42, fade tail over 0.95s — the arc visibly falls after the float.
- `skin` field: `Plain` (default) / `Diamond` — the two looks across the clips.
- Colour: Bone normal / Amber crit / Toxic-green poison.
- Known limit: numerals are UI-space — no distance shrink, no world-motion
  tracking once spawned. Both intentional.

## Elite plate

- Slim ink-well bar only — **no name text** (user request). `eliteName` is now
  purely the "is elite" flag (`label` param removed from `ShowPlate`).
- Anchor: **skinned-mesh top** (`SkinnedMeshRenderer.bounds.max.y`, cached once
  as `topOffset` at registration — animated bounds / a raised sword can't push
  the bar around) + `plateHeight` gap (0.08, clamped ≤0.6). Collider bounds are
  only the fallback — the 2m capsule overshoots the ~1.8m skeleton, which is why
  the collider-anchored version floated.
- Visibility: alive && (engaged || damaged) — trash mobs never register.
- `ProjectRestartEnemies` dresses enemy[0] as the Grave Warden (220hp/120 souls)
  and rewrites `plateHeight`.

## Spectral art sword ("Maliketh/Excalibur")

- `AttackController.artSwordSet` ← `BigSword.asset` (wired by setup tool).
- `TryStartArt` → `weaponSocket.ManifestArtSword(set, drawnFor)` — every art
  summons the blade, katana hides meanwhile, sword mirrors the equipped weapon's
  driven pose so it traces the same authored arcs.
- Spectral look: cloned materials (emission keyword + gold `EmissionColor`×2.2 +
  gold base) + point light (1.8 intensity, 2.6m) + scale pop-in/shrink-out.
  Cloned mats are destroyed in `ClearArtSword` — no leak; pedestal copy stays steel.
- Named `WeaponSpawned_Art` — inside `CollectPlacedWeapons`' "WeaponSpawned"
  skip, so `Equip` can never adopt it mid-swap.
- If big sword already equipped (pickup) → manifest no-ops.
- No smear/trail on the summoned blade — polish backlog, not a bug.

## VFX audit (all verified on disk)

`FX_SwordSlash_01`, `FX_Wind_Streaks_01`, `FX_Impact_Dirt_01`,
`FX_BloodSplat_Small_01`, `FX_Poison_Green_01`, `FX_ToxicSkulls`,
`FX_Lightning_Background_01`, `FX_Runes`, `Magic_Missile_FX`,
`SM_Prop_Skull_01–04`, `CLazy@WpJAttack_AirDrop_Keep/End` (clip `WpJAttack_*`,
Keep is looped).

Blood splat position fixed: pivot is capsule-CENTER — now chest (+0.4 up) on the
attacker-facing surface, spraying away. Two layers (slash streak + splat) is
intentional gore; blank `Health.bloodFx` if you want streaks only.

## Combat-feel pass (weight/trails/post)

- **Hitstop**: `freezeBase` 0.06 → `freezeMax` 0.11, backstab 0.16, art 0.12 —
  ~4–7 real frames at `freezeTimeScale` 0.02. Shake `0.2 + 0.1/combo`.
- **Swing tempo**: `animator.speed` piecewise on combo/variant swings
  (windup 0.85 → contact 1.6 → recovery 0.9; clock scales with it so windows
  land on the visual). Arts keep their `actCine` channel; dive/backstab excluded.
  Tune: `swingWindupSpeed`/`swingContactSpeed`/`swingRecoverySpeed`.
- **Step-in**: authored non-inplace root travel via the relay + windup
  facing/gap-close magnetism — already real; the contact-speed snap now makes
  the clip's lunge punch harder.
- **Slash arc**: REMOVED — a tip-only TrailRenderer can't draw a blade arc and
  read as mismatched (user call). The generic per-hit slash is gone too:
  `HitFx.Spawn` is pixel sparks only (no smear, no `FX_SwordSlash_01` burst —
  it couldn't track the swing). Per-art `FX_SwordSlash_01` fxCues STAY —
  they're clip-timed to `handR` so they follow the authored motion.
- **PostPulse**: always-on Bloom (threshold 0.8, baseline 1.2, scatter 0.6) +
  `Pulse` (bloom spike) + `AberrationPulse` (0.35 for ~0.09s on heavy connects —
  shake ≥ 1.4× base: finishers/backstabs/arts). Auto-creates on scene load.
- **Dodge**: directional roll-axis tilt (8°) on the dart; grounded momentum
  carry into locomotion/jump already existed via `SetLocomotionSpeed`.
- **Sprint focus-lines**: `GameHud` draws 6 `HudArt.Streak` needles — every
  ~60–85ms step each line teleports to a FRESH random spot on the screen
  perimeter (hashed arclength walk), re-aims inward, re-caps against the
  center mask, and re-rolls brightness/depth. Max alpha 0.15, center ~40%
  stays clear. Gate: `sprinting && !dashing || WallRunController.IsWallRunning`
  (wall-run freezes locomotion speed, hence the explicit flag). Wind-streak
  particle FX removed (world-space volume fought the camera). Sprint also
  holds `PostPulse.SustainAberration` ≈0.16 edge chroma.
- **Player blood**: removed — hits on the player shake the camera and crack the
  heart frame; no gore splat on the hero.
- **FOV**: already speed-scaled (base 55 → sprint 62 → slide/displacement 70).
- **Landing**: kick now scales with fall speed (`landingKick × clamp(-fall/14,
  0.5–2)`) — hops barely dip, ledge drops punch.
- **Flinch**: unchanged — `EnemyAI` staggers on every hit while `poiseMax` = 0
  (trash mobs); bosses drain the meter first. If a specific enemy doesn't
  flinch, check its poiseMax.

## Inventory / NPC / shop (megaplan phases 1–5)

- **ItemDef** (`Core/Items/`): id/name/shortName/icon/desc/kind/price/stackMax/
  effect/magnitude/projectile. `itemId` is save identity — never rename it.
- **Inventory**: stack ops + owned `WeaponSet`s + cycling quick-slot
  (F use · C,V/dpad cycle · Tab/I/select opens the menu). `Use()` = Potion-sip;
  `UseInstant()` = menu path (full effect, no anim). Auto-added by GameLoop;
  tool can pre-add one with `starterItems`.
- **UiGates.MenuOpen** = PauseMenu∪CheckpointMenu∪InventoryMenu∪ShopMenu —
  every gameplay listener (attacks, arts, flask, lever, checkpoint, camera
  look, item input) early-outs on it. PauseMenu keeps its own Esc gate +
  ClosedFrame pattern per menu.
- **PixelScrollList** (`Core/Hud/`): the masked wheel/keyboard-scroll row
  column extracted from CheckpointMenu — `Create/AddRow/AddGap/FinishRebuild/
  FirstInteractable/Tick`. Used by checkpoint side panel, InventoryMenu, ShopMenu.
- **InventoryMenu**: Tab/I/select or pause ITEMS row → real-time satchel —
  4 tabs (arrows/A,D/shoulders), scroll list left, icon+desc detail pane right,
  E uses/equips. ARTS tab is inspect-only (attune stays a checkpoint ritual).
  Saves on close. Gold panel frames (not the checkpoint Panel look); HUD
  quick-slot wears `HudArt.Frame(Kind.Plate)` — a dark plate, no white fill,
  distinct from the violet art sockets. Menus ignore input on `openedFrame` —
  the opening keypress must not read as the menu's own close/confirm.
- **NpcMerchant + ShopMenu**: `E TALK` within radius → bark toast + BUY/SELL
  (sell = consumables at half price; key items can't be fenced). Saves on close.
  `Setup Shop + Inventory` authors 6 ItemDefs, builds Merchant.controller
  (CLazy idle → Synty humanoid), plants the stall by Checkpoint A.
- **SaveGame** (`persistentDataPath/save.json`, v1): souls, item stacks
  (`id:count`), owned weapon names, equipped set. Written on rest / death /
  menu close / quit; loaded in `GameLoop.Start`. Unknown ids skipped so
  tool-authored assets can be renamed freely.
- Self-checks: `ShopInventory` covers stack caps, consume-drop, serialize
  round-trip, unknown-id skip, ownership dedupe.

## Open decisions

- **Enemy theme**: Synty skeletons (current) vs reference crow-swarm/palette
  swaps — owner decision, untouched.
- **WeaponPickup**: still placed — if big sword becomes arts-only, remove the
  pedestal (`EnsureWeaponPickup` in the combat tool).
- **Damage skin**: Plain default; flip `DamageNumberSpawner.skin` for diamond.

## Play-test checklist

1. `Tools ▸ Project Restart ▸ Setup Combat Locomotion` (dive states, art sword,
   dash streak, dive impact FX).
2. `Setup Skeleton Enemies + Nav Grid` (elite + plateHeight write).
3. `Setup Shop + Inventory` (ItemDefs, merchant + stall, starter vials).
4. `Run Souls Self-Checks` (DiveAttack/DiveLand, Art9, pickup, icons, inventory).
5. Feel pass: slide→dash, dive arc + land flourish, wall-dash burst between the
   two arena walls, Toxic Chorus, art sword pop, numeral readability, plate height.
6. Inventory pass: Tab opens ITEMS (all 4 tabs, scroll, detail, E-use/equip),
   F uses the HUD quick-slot, C/V cycle, Esc→ITEMS from pause, E TALK at the
   merchant → buy/sell flows, die+respawn keeps items, quit+relaunch restores.
