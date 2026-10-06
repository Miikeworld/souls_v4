# NEA — implementation completion and combat-feel correction plan

Date: 3 October 2026  
Project: `D:\souls_v4`  
Status: implementation brief, not completed work. No item is verified merely because it appears in this document.  
Companion: [COMBAT_DIRECTION_PLAN.md](COMBAT_DIRECTION_PLAN.md)

## 1. Purpose and priorities

Complete the agreed combat design and correct the gaps visible in the 3 October recording. Preserve the spinning attacks and linked sweeping combos the user already enjoys. The goal is reliable, expressive crowd combat with optional aerial routes, readable dark-crimson blade effects and useful movement between contacts.

The grounded loop is the foundation:

**Approach → spin/sweep → follow-up technique → normal attacks or dodge → reposition → another technique → kill mana.**

The aerial loop is an additional route:

**Two normals → uppercut → rise with up to three enemies → three separately controlled aerial strikes → optional spike/plunge → grounded continuation.**

This document operationalises the companion plan. Its explicit details resolve implementation ambiguities; it does not authorise cutting the remaining requirements or replacing the fun ground chains. If the code has changed since inspection, reconcile the current implementation with these behaviours before editing. Preserve newer working fixes.

### Evidence and limits

- Latest recording: `C:\Users\milkw\Videos\Captures\souls_v4 - 00_TestBlockout - Windows, Mac, Linux - Unity 6.3 LTS (6000.3.23f1)_ _DX12_ 2026-10-03 14-03-26.mp4`.
- References: `C:\Users\milkw\Downloads\DcB7XBIzwvO_1.mov` and `C:\Users\milkw\Downloads\DbtUy-pBVU2_1.mov`.
- Latest footage around 0:10–0:15 demonstrates the crowd spins worth preserving. Around 0:50–0:53, elevated travel separates the Hero from useful contact with a stationary target. Around 1:52–1:55, vertical movement does not demonstrate the planned sustained aerial exchange.
- The boss was intentionally disabled for the recording. Its lack of movement is not a defect and must not trigger an unrelated boss-AI rewrite.
- Magenta enemies, uniformly bright-red blade presentation and competing orange streaks remain visible. Audio, precise input latency and complete controller acceptance were not established by the frame review.

### Current implementation gaps confirmed by inspection

| Area | Observed local implementation | Required result |
|---|---|---|
| Launcher | Two normals select Upper Attack; setup links it directly to Skyfall Edge | Successful launch creates a controlled aerial session and player-selected continuation |
| Aerial selection | Nearby airborne targets within 7m can override RMB | Only the player's successful launch session grants aerial combo/chase context |
| Requests | A request list exists; ComboBranch includes pending normal swings | One pending request; branch reflects the committed normal step |
| Ultimate | Q+RMB chord can refund/cancel the first action | Dedicated input and earned meter; no retroactive supersession |
| Drinking | Sets IsRooted and uses base-layer potion states | Slow movement beneath a masked upper-body drink |
| Aiming | Lock target generally wins at attack entry | Explicit movement-led, target-led and launch-session policies |
| Spin contacts | Timer-based rehitInterval | Validated blade-pass contacts retaining repeated-hit feel |
| Feedback | Normal hitstop defaults remain 0.09–0.17s | Lighter contacts and one aggregated event per contact beat |
| Resources | Passive mana regeneration path remains | Kill-led economy and bounded boss recovery |
| Presentation | Missing-material appearance persists in footage | All spawned variants and owned effects render correctly |

## 2. Preserve the ground-combat baseline first

Before changing timing, capture the current Tide Splitter → Ashen Cleave → Stonebreaker and dodge → Iron Gale → Molten Arc sequences against the same five-enemy arrangement. Record action start/end, contact events, resource changes and blade visibility. Use those captures for before/after comparison.

- Keep full authored spins, sweeping arcs, acrobatic poses and useful root travel.
- Keep neutral RMB as Tide Splitter, including while sprinting.
- Keep current fast playback for spins and sweeping cuts initially. Do not apply heavy cinematic windups to every technique.
- Preserve multi-hit spin rhythm; do not convert the entire spin into one damage event.
- Connect requested techniques directly at their safe exit. No forced idle stance, sword disappearance or sheathe/draw interruption between links.
- Allow normal attacks or dodge after the final required contact; do not force completion of an entire technique chain.
- Intermediate reactions keep enemies within follow-up reach. Strong scatter and knockdown belong mainly to finishers.
- A standard crowd encounter must remain completable and satisfying with grounded normals, techniques and dodges alone.

## 3. Input router, route selection and transactional actions

### Default bindings

| Action | Keyboard/mouse | Controller |
|---|---|---|
| Normal attack | LMB | RT |
| Technique | RMB | RB |
| Modifier | Hold Left Alt | Hold LT |
| Selected art | Q | LB |
| Ultimate | T | D-pad up |
| Drink | R | West face button |
| Flask mode | Existing wheel selection | Modifier + west face button |
| Art selection | 1/2/3 | Modifier + D-pad left/right cycles three slots |

Unmodified D-pad left/right continues cycling quick items. Preserve existing movement, lock-on, interaction and dodge bindings. Remove west-face-button attack from the runtime action clone to eliminate its flask conflict. Expose new combat bindings in the existing controls page; do not edit the template input asset.

Use one combat input router. Modified input consumes its unmodified counterpart. Sample modifier state at press time; pressing it alone does nothing. Holding attacks does not repeat or charge. Remove Q+RMB chord recognition and TryBurstArt's refund/cancel supersession path.

### Ground routes

| Context | Result | Subsequent unmodified RMB |
|---|---|---|
| Neutral/moving/sprinting RMB | Tide Splitter | Ashen Cleave → Stonebreaker |
| First committed normal → RMB | Grave Wolf I | Grave Wolf II → Grave Rend → end |
| Second committed normal → RMB | UpperAttack | Session-specific aerial continuation after success |
| Third or later committed normal → RMB | Red Reaver | Bonesunder → end |
| Modifier + LMB | UpperAttack | Session-specific aerial continuation after success |
| Modifier + RMB | Full Grave Wolf | End |
| Dodge recovery → RMB | Iron Gale | Molten Arc → end |
| Dodge recovery → modifier + RMB | Mooncleaver | End |
| At least 0.6s uninterrupted sprint → LMB | Mooncleaver Rush | End |
| Dedicated ultimate input | Last Eclipse | Rage on successful completion |

This preserves all fifteen original Big Sword entries. UpperAttack is additional. Remove Grave Rend → full Grave Wolf from setup; the full chain has its own modifier entry. Mooncleaver is a stationary rising/falling strike, not the launcher. Skyfall Edge is the explicit aerial spike.

Resolve inputs in this order: UI/death/action restrictions; airborne/session context; modified commands (including modified dodge route); valid running-technique follow-up; unmodified dodge context; committed normal branch; neutral route. Sprint affects LMB only. Locked staggered targets and unrelated airborne enemies never substitute another RMB technique.

The dodge context lasts through dodge recovery and 0.2s after completion. Normal branches use the current committed normal index, never the number of future queued clicks. No automatic chaining: each link needs a press. At the end of a technique route, unmodified RMB may request neutral Tide Splitter at the normal safe exit, subject to payment/cooldown.

### One pending request

Replace the request list with one pending action expiring after 0.25 gameplay seconds. Stop its age during owned impact freeze and ultimate presentation; menus clear it. Capture action ID, branch, target and modifier when pressed.

- Latest valid request replaces the pending request; simultaneous aerial LMB/RMB is one request.
- Validate animation, ownership, resources, cooldown and required target before cancelling the source.
- Pay and start cooldown only at commit. No refunds used to disguise a cancelled first half of a chord.
- Failed or expired requests preserve the source and spend nothing.
- Normal attacks after a technique restart the normal combo.
- Default exit is final required contact/projectile release + 0.06s. Plunge retains its explicit recovery thresholds.
- Multi-window attacks cannot exit before their final required window. An input can request the next move without cutting off the current spin's final pass.
- Death, teleport, disable, scene change and menu entry clear pending input and action-owned effects.
- Reject attack requests during committed drinking; do not replay them after the sip.

## 4. Uppercut and aerial session

### Motion and ownership

Use Big Sword UpperAttack as the source-motion reference and its ZeroHeight variant for the controlled launcher. Do not combine authored root-Y travel, chase lift and locomotion gravity. One aerial-session owner controls capsule vertical motion; RootMotionRelay consumes allowed planar root travel only.

Initial launcher tuning: 24 damage, 30 poise, 14 stamina, no mana, 2.6m reach, 120° frontal contact. Author contact on the actual rising blade. A genuine poise break on this hit counts as vulnerable; obtain that result from damage resolution before poise regeneration/reset obscures it.

Launch up to three living vulnerable contacts. Prioritise a valid locked contact, then bearing/distance; additional victims are nearest valid contacts. Enemies outside the cap receive normal damage/reaction. Bosses remain grounded.

- Start Hero and victims at 8m/s upward velocity with 14m/s² gravity.
- Cap supported rise at 3m above the launch floor.
- Use collision-safe capsule movement and respect ceilings.
- Establish the session only after a living enemy is successfully launched.
- On miss, kill-without-survivor or resistance, complete a short cosmetic hop capped at 0.35m, then ordinary falling/landing; no aerial combo permission.
- No parenting, teleporting victims into formation or launching corpses to gain session access.
- Retain the Big Sword manifestation through launch, air actions and owned landing recovery.

### Three deliberate aerial attacks

Use Big Sword Jump_Attack_Combo_ALL as a continuity reference. Play its numbered parts 1/2/3 as distinct steps; do not substitute Skyfall for the entire chain or trigger ALL automatically on one press.

| Step | Damage | Stamina | Initial duration |
|---|---:|---:|---:|
| Aerial 1 | 18 | 8 | Approximately 0.4s |
| Aerial 2 | 20 | 8 | Approximately 0.4s |
| Aerial 3 | 26 | 10 | Approximately 0.4s |

Either attack button advances one step. Holding does nothing. Use 0.06s entry/chain blends initially and source-derived contact/trail windows. If the parts differ from the ALL take at their joins, author project-owned transitions/slices rather than editing vendor clips.

Confirmed contacts arrest downward motion of the Hero and struck session victims for at most 0.12s per step, 0.36s total. No added height. Misses provide no suspension. End support after three strikes or 2.4s since launch, excluding owned global hitstop. No midair relaunch or new aerial chain before landing.

Track one primary living victim. Allow horizontal correction up to 2m/s while moving toward a non-overlapping striking position; walls stop correction. Never snap orientation toward a new unrelated enemy mid-contact. If the primary dies, select another living victim from this session; otherwise finish the current motion and fall.

### Explicit exits

- Session LMB/RMB: next aerial strike.
- Session modifier + LMB: plunge toward the floor.
- Session modifier + RMB: Skyfall Edge against a valid living session victim.
- No press: finish current motion and fall.
- Ordinary jump/wall exit without a session: LMB/RMB requests plunge; modifier + RMB has no Skyfall target and is denied.
- After all three steps, further ordinary attacks do not restart the air combo. Modifier finishers remain available until landing if their gates pass.

Skyfall applies its authored strike/spike once. It does not also emit the separate 32-damage plunge slam. In-place and travelling Mooncleaver retain their distinct identities; no automatic Skyfall → Mooncleaver Rush chain.

### Landing and collision cleanup

Preserve the established animated plunge flow: 0.35s preparation, 0.12s entry blend, inherited vertical brake over 0.08s, visible fall loop, acceleration to -18m/s over 0.18s and steering capped at 1m/s. Recovery uses AirDrop_End at ×1.2, combat exit at 0.16s and movement exit at 0.24s.

Use walkable capsule-bottom contact. Enemy bodies are not floor. Restore every temporarily ignored collision pair on landing, cancellation, disable and death, preserving its original ignore setting. Resolve residual enemy overlap without moving through walls.

Plunge emits one 32-damage, 2.6m slam and one surface-aligned impact even on empty ground. Aggregate victim contacts into that event: 0.06s freeze, shake 0.25–0.35 and particles lasting at most 0.6s. Other aerial landings do not inherit this damage automatically.

FootGrounding remains the sole visual-root-Y owner. Hand locomotion a resolved landing without granting another jump or replaying the slam after walking off an edge.

## 5. Contacts, targeting and animation travel

Add explicit ActionFamily, AimPolicy and ReactionProfile data; do not derive gameplay identity from DarkCrimson VFX. Keep existing WeaponArt assets/GUIDs and normal WeaponSet data.

Aim policies:

- Movement-led: katana crowd normals, Tide Splitter, Ashen Cleave, Iron Gale, Molten Arc and broad Big Sword normals. Movement deadzone 0.2; neutral input falls back to lock bearing, then current facing.
- Target-led: Grave Rend, Red Reaver, Bonesunder, full/split Grave Wolf and projectile arts. Prefer valid lock bearing, otherwise movement/current facing.
- Launch-session: aerial continuation follows the session primary. Uppercut entry uses target-led selection within its real frontal contact.
- Limit anticipation correction to 360°/s. After first contact keep the chosen bearing, except authored body/root rotation. Do not counter-rotate the Hero to face the target during a spin.

Consume root travel exactly once through CharacterController. Do not add synthetic forward push to a root-driven clip. Review capsule path, blade path and enemy contact together for Mooncleaver Rush and other leaps. Correction must not silently convert the stationary Mooncleaver into a travelling attack.

Preserve katana's 2.6m, 180° contact volume and existing technique damage/poise/reach initially. Structured contact results must include actual damage, poise break, kill and reaction eligibility. Deduplicate by health owner, check height/solid obstacles and process crossed narrow windows at low frame rates.

Replace Tide Splitter's arbitrary timer re-hit with up to four genuine authored blade passes. Maintain approximately 40 total damage/36 total poise across those passes. Each victim can be hit once per validated pass. Do not create four windows if the animation only supports fewer distinct passes; redistribute total damage without speeding or cutting the spin to fit a number.

Reaction targets: normal 0.2m/0.2s; sweep up to 0.5m/0.3s; spin intermediate displacement no more than 0.15m per pass; heavy finisher up to 0.8m/0.6s; spike landing approximately 0.7s. Respect poise and preserve useful follow-up reach. Directional flinch should show the blade's consequence rather than identical recoil on every move.

## 6. VFX, audio and camera

### Repair before additional decoration

Inspect every instantiated skeleton variant, its material slots and its corpse renderers. Fix project-owned assignments/variants with compatible URP materials. Do not blindly replace Fantasy Hero's colour-mask shader or edit vendor materials. Check prefab creation, respawn and pools; a material fix on only one scene instance is insufficient.

Inspect each project-owned particle renderer and trail material for null, missing or unsupported shaders. Keep vendor GUIDs and source assets intact.

### Big Sword presentation

- Keep 1.45× spectral scale initially, correct palm calibration and the shared BladePoseResolver.
- Dark blade body with a narrow crimson edge and sparse pale highlights; remove the uniform fully luminous red surface.
- Sample after final weapon pose. Trail lifetime 0.10s, Last Eclipse 0.14s; alpha at most 0.55.
- Preserve continuous readable blade arcs during real spins. Clear sampling history between disjoint gates/actions and on teleport, cancellation or swap.
- Keep the manifested weapon through connected techniques, while resetting effect ownership at handoff.
- Heavy finishers gather force then release quickly; spinning/linked cuts retain their fast baseline and are not given additional charge holds.
- Arc Blade remains the dedicated wave: 15 mana, 1.6s cooldown, 30 damage/25 poise, 18m/s, 12m maximum travel, swept 2.4m × 1.6m piercing shape stopping at solids. Projectile damage only.
- Last Eclipse gets a compact surrounding dark-crimson eruption during its actual contact window. Decorative slashes create no extra damage or lingering hazard.
- Shorten the oversized orange contact streaks. One contact group can emit at most three full particle bursts, chosen nearest the Hero, with lightweight sparks for remaining victims.
- Aggregate rapid damage numbers per victim/action within 0.12s, preserving crit/poison classes. Do not suppress the visible progression of a multi-pass spin entirely.
- No enemy blue contact slices, radial blur or persistent ground damage without authored contact windows.

Hitstop targets: quick 0.04s, heavy 0.07s, final 0.10s, plunge 0.06s. Spin uses 0.02s on its first successful pass and 0.05s on its final successful pass; intermediate passes use reactions, sparks and sound. Do not stack freezes across victims. Camera shake uses the strongest event in a contact group, not the sum.

Inventory and wire existing licensed whoosh, contact and landing sounds through SfxBank. Missing audio is an explicit outstanding item, not a silent PASS. Judge audio in live playback separately from frame captures.

Camera: retain player control and current base distance/FOV initially. Add clamped, smoothed vertical framing of the Hero/primary victim midpoint; clamp additional focus rise to 1.5m, blend in over 0.15s and out over 0.25s. Retain collision checks and avoid forced overhead pitch or ultimate orbit. Attack FOV must not inherit slide FOV. Reassess distance only after the target-relative motion is correct.

## 7. Mana, ultimate and rage

Preserve current technique costs/cooldowns for the first tuning pass. UpperAttack and aerial normals use the stamina costs above. Remove passive mana regeneration and migrate saved scene values explicitly.

- Trash kills restore 6 mana, elite kills 12, with existing player attribution and one genuine-death award.
- Boss normal melee contacts restore 2 mana once per action and at most once per 0.5s. No technique, projectile, invulnerable or damage-over-time farming.
- Checkpoints and flasks retain their existing restoration roles.
- Verify a full starting mana pool supports Tide → Ashen → Stone and kill returns support subsequent combat. Do not nerf spinning chains by adding special cooldowns or higher costs.

Ultimate resource: 100 capacity, 3 per successful normal action, 4 per successful technique/art, plus 4 per trash kill or 8 per elite kill; maximum 12 per originating action including delayed kills. Track origin IDs so multi-window/projectile/DoT paths cannot bypass the cap. No gain from Last Eclipse or during rage. Reset on death/rest/load; no passive gain/decay.

T/D-pad up requests Last Eclipse using the shared safe-exit rules. Validate first, then spend 100 meter exactly once; no mana payment. Do not cancel and refund an already-started Q/RMB action.

Keep world slowdown at 0.05 during the authored windup, with consistent unscaled Hero animation/action/particle clocks. No camera pan; banner remains off. Use owned time/iframes so cleanup cannot clear another system's protection or time setting. Existing cast protection ends with the cast; rage has ordinary vulnerability.

Successful completion starts 15s of Big Sword rage. Preserve seven-hit normals and all technique routes. No blanket speed multiplier or invulnerability. Pause rage duration during owned hitstop, not during non-pausing menus. Defer restoration until a safe action exit; manual weapon change ends rage without restoring over that choice. Death cancels it.

## 8. Moving drinks

Implement a masked upper-body Animator layer for Potion_Drink/Potion_Empty over active walking/strafe locomotion. Preserve native potion-clip avatar rules. FootGrounding remains active on the base layer.

- Walk/strafe at 50% normal walking speed; neutral input stays still.
- No sprint, jump, attack, dodge, slide or wall-run during the committed sip.
- Begin only grounded and outside incompatible actions. Capture HP/mana mode at start.
- Spend one charge immediately. Preserve 1.6s sip and the 35–75% swallow restoration interval.
- No voluntary cancellation/refund. Ordinary damage retains current non-interruption behaviour; no iframes.
- Death, disable, teleport or genuine loss of ground stops remaining restoration without refund and cleans bottle/layer state.
- Empty sip permits the same slow movement and spends nothing.
- Bottle owns its hand; stow/hide the weapon during the sip and restore the current legitimate weapon owner afterward.
- Use explicit drinking restrictions, not global IsRooted writes. Never clear another action's root flag on drink completion.
- Menus remain non-pausing: a committed sip advances and cleans up normally while input is gated. Opening a menu cannot freeze restoration indefinitely.

Update controller generation and Step 5 verification together so a rerun preserves the new masked layer instead of rebuilding the old single-layer controller. Verify Step 6 speed/grounding expectations with the temporary drink movement cap.

## 9. Courtyard, enemies, UI and persistence

Create a separate project-owned courtyard scene without rebuilding the existing level. Approximately 24m × 20m, two nearby fighting pockets, two marked wall-run surfaces, one low plunge platform, stairs/slopes and restrained perimeter ruins. Use existing assets and a clear central combat floor.

Provide resettable encounters: three light melee enemies; four light plus one elite; an optional eight-enemy stress group. Standard fights allow two simultaneous melee commitments with at least 0.25s between strike starts. Waiting enemies reposition; death, stagger, launch and disengagement release reservations. Do not apply this coordinator to disabled test bosses or overwrite boss brains.

HUD: preserve Persona styling and three Q sockets. Add a compact ultimate meter, modifier-held command hints and session aerial-step/finisher hints. No floating enemy HP bars. Update the controls page with every route and actual active bindings.

Preserve save data, art GUIDs, icons and unrelated libraries. Explicit action-family migration replaces visual-theme-derived classification. Resolve saved Q slots to stable art identity before library filtering; write a versioned migration that preserves valid existing selections. Do not delete orphan/custom arts during setup.

Setup remains manual after compilation/import and outside Play Mode. Make reruns idempotent, with explicit versioned field migrations rather than relying solely on renamed serialized fields. Do not run another Unity instance or write a loaded scene on disk then allow Unity to overwrite it accidentally.

## 10. Delivery gates and required evidence

### Gate A — preserve and stabilise grounded combat

Input router, one pending request, explicit action metadata, predictable routes, protected spin continuity and normal/dodge exits. Demonstrate Tide → Ashen → Stone and Iron → Molten before adding aerial features. Verify no nearby airborne/staggered target steals RMB.

### Gate B — prove the aerial loop

Uppercut → separately pressed aerial 1/2/3 → optional spike/plunge → clean landing. Demonstrate one enemy, then three, then resistant elite/boss, miss, kill, wall, ceiling and ledge cases. Show no height farming or unrelated target acquisition.

### Gate C — restore readability and grounded ownership

Repair materials, audit all blade poses, author genuine contact/pass windows, tune aggregated feedback, camera and moving drinks. Verify no visual-root sinking, duplicate movement owners or weapon respawn between links.

### Gate D — complete resources and encounter slice

Earned ultimate, rage, mana economy, HUD, courtyard and coordinated enemy pressure. Prove grounded-only crowd completion and separate boss compatibility. Preserve the disabled boss configuration unless deliberately testing an enabled copy.

### Compile and focused checks

Run dotnet build for Assembly-CSharp.csproj and Assembly-CSharp-Editor.csproj. Add scripts manually to project files until Unity regenerates them. Focused tests must cover:

- Input precedence, simultaneous presses, single pending request, expiry, hold behaviour and no branch inflation from queued normals.
- Transactional resource/cooldown gates and failed requests preserving the source.
- All fifteen skills reachable; full Grave Wolf not replayed after the short route.
- Health-owner/window deduplication, crossed narrow windows at 30/60/120 FPS, height checks and obstacle blocking.
- Three-victim cap, poise-break eligibility, corpse exclusion, bounded lift/suspension and target loss.
- Projectile-only wave damage, collision sweep and wall stop.
- One floor slam/payment/feedback event; cleanup of collision ignores and blade history.
- Ultimate gain caps across delayed contacts; no chord refund, rage gain farming or time/iframe ownership leaks.
- Drink mode capture, immediate charge cost, partial restore on interruption, blocked actions and continued leg locomotion.
- Setup rerun preserving layers, all skill identities, non-project arts and existing attunements.

### Live review matrix

Review all fifteen Big Sword skills plus UpperAttack and aerial parts 1/2/3 on Hero and the correct source rig. For each record: entry pose, contact windows, full blade rotation, palm grip, root/capsule travel, trail gates, recovery exit and effect cleanup. Repeat with katana and Big Sword equipped. Do not mark contactMotionReviewed simply because an asset table or compile check passed.

Test keyboard/mouse and controller independently. Include ordinary jumps (plunge only), rising/falling plunges, high drops, stairs, slopes, walls, platform edges, death, teleport, weapon swap, menu entry and repeated setup. Test HP and mana drinking while moving forward, backward and strafing.

Deliver before/after clips of the two protected spin chains, the deliberate aerial chain, moving drinks and a five-enemy encounter. Record exact controls and resources so pauses can be distinguished from failed inputs. Listen to audio in actual playback.

Use the supplied references to judge movement continuity, blade readability and useful exits, not to justify indiscriminate speed increases. Preserve the enjoyable rotations throughout the comparison.

### Completion reporting

Provide a checklist with Passed / Failed / Not tested, separated into code/static, Editor setup, live visual, keyboard/mouse and controller evidence. Include exact commands and remaining defects. A successful compile is not proof of contact timing, correct materials, satisfying feel or device support.

If an animation, sound or live-test capability is unavailable, state the missing item and leave the relevant acceptance check open. Do not claim the entire plan is implemented because the launcher exists. The implementer owns timing authoring and all skill reviews; the user is not expected to supply contact fractions.
