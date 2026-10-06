# Boss pass — attack sequences + weapon placement

## What was broken

- **Random coin-flip picks**: `PickMove` rolled `Random.value < 0.5` per move — no distance
  awareness, no anti-repeat, no weights. Jab-Jab-Jab streaks and whiffed swings were normal.
- **Jab could whiff by design**: its range (3.2 m) was shorter than `engageRange` (3.4 m) —
  the golem stopped walking at 3.4 m and could pick a punch that mathematically missed.
- **No gap-closer**: `walkSpeed` 1.6 vs player run ~5 m/s — both bosses were kiteable forever.
  `Dash_Attack_ver_A` sat unused in the Grruzam pack.
- **Homing swings**: `Face(dt * 0.4)` ran for the whole attack clip — every swing steered
  mid-arc, so strafing could never make a swing miss. Not Souls rules.
- **Dead moves (BossLord)**: `Teleport` had `range = 0` → `dist <= range*1.15` was never true →
  the ninja-phase blink and the P3 teleport→Pyre chain could never fire.
- **ShadowJabs never chained**: three independent entries meant Jab3 could fire before Jab1.
- **Weapons at the wrist**: both tools parented the weapon prefab to `Hand_R` at identity —
  the grip point sat on the wrist joint, not in the palm (the exact bug `WeaponSocket.palmGrip`
  fixed for the player).

## New selection model (both bosses)

Every move now declares a **distance band** (`pickMin`/`pickMax`), a **weight**, and a
**commit point** (`trackAt`):

- Chase runs until ANY ready move's band brackets the player (`ReadyAny`) — not a fixed
  engage bubble.
- `PickMove` = weighted roll over moves that are off cooldown AND in band; the last move
  used is excluded when ≥2 candidates remain (anti-repeat).
- Tracking stops at `trackAt` (normalized time, default = first hit window). After the
  commit point the swing is fixed — sidestepping/rolling wins. Per-move values tune how
  "sticky" each windup is.

## FortGolem — "Warden of the Deep Ruin"

Slow, deliberate, heavy. All clips slowed (0.6–0.9×) for mass; root motion carries attacks.

### Phase 1 — tests your guard

| Move   | Band (m) | Reach | Arc | Dmg | CD  | Window     | Track until | Read |
|--------|----------|-------|-----|-----|-----|------------|-------------|------|
| Jab    | 0–3.0    | 3.0   | 130 | 14  | 1.1 | 0.36–0.50  | 0.30 | quick poke at point-blank; weak, fast |
| Swing  | 1.5–4.6  | 3.8   | 170 | 24  | 1.6 | 0.44–0.60  | 0.36 | wide sweep — roll through it |
| Crush  | 2.0–4.8  | 4.0   | 110 | 40  | 3.4 | 0.55–0.68  | 0.45 | slow overhead — biggest telegraph, biggest punish |
| Charge | 4.2–11   | 5.0   | 80  | 30  | 4.5 | 0.42–0.58* | 0.20 | NEW: dash-attack lunge; commits early — sidestep it |

### Phase 2 (HP ≤ 50%) — Roar, then "Awakened"

Everything from P1 (faster cooldowns, +dmg) plus:

| Move    | Band (m) | Reach | Arc  | Dmg | CD  | Windows            | Read |
|---------|----------|-------|------|-----|-----|--------------------|------|
| Barrage | 0–4.2    | 3.6   | 140  | 17  | 3.4 | 0.30–0.42, 0.62–0.75 | two-hit string — dodge through or stay out |
| Slam    | 0–5.2    | radial 5.2 | 360 | 30 | 5.0 | 0.52–0.58          | anti-hug shockwave + ground FX |
| Charge  | 4.0–12   | 5.0   | 80   | 32  | 3.5 | 0.42–0.58*         | comes more often, reaches further |

*Charge's hit window is a guess from the clip family — tune after watching it in Play.

## BossLord — "Vess, Hollow Blade"

Three phases, human-speed. Same band/weight/commit model.

### P1 samurai
| Move       | Band    | Dmg | CD  | Read |
|------------|---------|-----|-----|------|
| DrawSlash  | 0–4.4   | 26  | 1.6 | fast horizontal, weight 1.2 |
| TwinCut    | 0–4.2   | 20  | 2.2 | two windows (0.28 / 0.60) — wait for BOTH |
| HeavenCut  | 1.5–4.6 | 38  | 3.6 | slow riser, rare (0.7) — the punish bait |
| RushDraw   | 4.0–10  | 30  | 5.0 | iado-style closer; no longer picked at melee range |

### P2 ninja (HP ≤ 50%) — Roar "UNSHACKLED"
- **ShadowJab 1 → 2 → 3** now chains for real: only Jab1 is ever picked (0–3.8 m); each
  link enters the next only while you stay inside `chainRange` (4 → 3.6 m). Roll out
  mid-string and the follow-up whiffs into air.
- **Teleport actually fires now** (was unreachable): blink to your flank, weight 0.8.
- Kunai fan at 3–14 m; DrawSlash/TwinCut still in the pool.
- Reactive `Dodge` quickstep on hit (30%, unchanged).

### P3 magic (death interrupt — revive at 40%)
| Move      | Band     | Read |
|-----------|----------|------|
| PyreCast  | 5–18     | 5 homing fireballs — no point-blank casts |
| AshSlam   | 0–5.5    | radial, anti-hug |
| EmberEdge | 0–4.6    | empowered melee |
| Teleport  | 2–16, cd 4 | now followed by PyreCast 50% of the time (was impossible before the fix) |

## Weapon placement

Both `ArmWeapon` helpers now socket like `WeaponSocket.palmGrip`: the weapon's pivot
(its grip) sits on the wrist→middle-knuckle line at `palmGrip = 0.55`, resolved from
`GetBoneTransform(RightMiddleProximal)` on the humanoid avatar — not the wrist joint.
Rotation stays identity (vendor-matched prop/rig), scale forced to 1.

The setup logs the resulting `localPos`/`localRot` — if a grip still reads wrong in Play,
tune `BossWeapon`/`LordWeapon`'s local transform directly in the scene (parented under
`Hand_R`, the local pose persists; the log line is the seed to iterate from).

## Unity steps (re-run required)

1. `Tools > Project Restart > Setup FortGolem Boss` — adds the `Charge` state to
   `BossBase.controller` + re-sockets the greatsword at palm.
2. `Tools > Project Restart > Setup Dark Lord Boss` — re-sockets the ornate blade.
3. Playtest list:
   - weapon sits in the palm through locomotion AND swings (not floating at the wrist)
   - golem lunges with `Charge` when you back off past ~4 m — sidesteps beat it
   - swings no longer track you mid-arc after the commit point
   - Vess teleports in P2 (new) and chains ShadowJab 1→2→3 (new)
   - Vess P3: teleport → PyreCast follow-up
   - hit windows still match the clips (Charge's 0.42–0.58 is the guessiest — check first)

## Honest limits

- Hit windows are still hand-tuned normalized times, not clip events — `Charge` most of all.
- Weapon local rotation is the vendor-identity guess; can't verify the blade axis without
  Play Mode. The log line + inspector editing is the tune loop.
- Tracking commit is uniform per move — no per-window re-track (TwinCut won't re-aim
  between its two hits, which is intended Souls behaviour).
