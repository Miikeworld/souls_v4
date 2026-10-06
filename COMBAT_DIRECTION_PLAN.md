# NEA — connected ground and aerial crowd combat

Date: 3 October 2026  
Status: agreed implementation plan; saving this document does not implement or verify its changes.  
Project: D:\souls_v4  
This replaces the earlier combat-direction draft. Tuning below is an initial implementation target, not a claim of playtested results.

## 1. Direction and agreed decisions

Build a complete combat slice around the spinning attacks and connected technique combos the player already enjoys:

**Move into a group → spinning sweep → chained cleave → normal cuts or dodge → another technique → kills restore mana.**

The aerial route is an additional choice: **normal cuts → Big Sword uppercut → rise with enemies → aerial combo → spike or plunge → return to ground combos.** Staying grounded must remain a complete, effective and enjoyable way to fight.

The player should move through encounters with clear control over attack choice. Individual techniques retain their authored motion; speed increases and larger effects must not conceal weak transitions.

Decisions incorporated:

- Preserve the existing fun of spinning attacks, broad sweeps and repeated-press technique chains. Improve their connections and readability without replacing them with isolated heavy attacks.
- Tide Splitter stays available on neutral RMB, including while sprinting. Neither nearby airborne enemies nor sprint context may steal that input.
- Ground combo preservation takes priority over new launcher, aerial and ultimate features.
- Fixed, learnable technique routes rather than checkpoint technique loadouts.
- A held modifier opens additional commands.
- The uppercut and aerial combo always use Big Sword, including when katana is equipped.
- Aerial combos require a successful launcher; ordinary airborne attacks remain plunge.
- Either attack button advances the aerial combo, one strike per press.
- Uppercut launches up to three eligible enemies.
- Sweeps favour movement direction; focused attacks favour the locked target.
- Ultimate uses a separate earned meter and leads into mobile Big Sword rage.
- Mana supports aggression through kills, with a limited boss recovery rule.
- Drinking allows a slow, committed walk. Charges are spent immediately.
- Use Black Blade-inspired blade/wave presentation for selected moves and a Destined Death-inspired surrounding eruption for Last Eclipse.
- Include a separate styled courtyard and controlled crowd encounters. Existing bosses receive compatibility work, not full moveset redesigns.

Existing clips and project assets are the starting point. Preserve vendor assets, GUIDs, character appearance, saves, Persona UI and established avatar rules.

## 2. Controls, routes and aerial flow

### Input ownership

Introduce one combat input router so the same press cannot trigger several systems.

Default controls:

| Command | Keyboard/mouse | Controller |
|---|---|---|
| Normal attack | LMB | RT |
| Technique | RMB | RB |
| Modifier | Hold Left Alt | Hold LT |
| Selected art | Q | LB |
| Ultimate | T | D-pad up |
| Drink | R | West face button |
| Change flask type | Existing mouse wheel | Modifier + west face button |
| Previous/next art slot | Existing 1/2/3 selection retained | Modifier + D-pad left/right |

Unmodified D-pad left/right continues cycling quick items. Modified inputs consume the corresponding unmodified action. Remove the runtime normal-attack binding on the west face button so drinking cannot also attack.

Holding the modifier alone performs no action. Its state is sampled when an attack is pressed; no simultaneous-press tolerance or delayed chord recognition. Remove Q+RMB ultimate detection and refund/cancel supersession.

Keep normal attacks immediate and hold-to-repeat disabled. Expose combat bindings in the existing controls UI without editing the template input asset.

### Ground routes

“After a normal” means during that normal’s valid buffering/recovery period. It is not a rapid-click timing puzzle.

Preserve the existing E → B → D spin/cleave/slam chain and F → H acrobatic sweep chain. Each RMB press asks for the next technique; the player can instead return to normals or dodge at the safe recovery point. Modifier commands add options without moving the core spin behind a modifier, a launcher or an ultimate.

| Input/context | Move and subsequent RMB route |
|---|---|
| Neutral RMB | Tide Splitter → Ashen Cleave → Stonebreaker |
| One normal → RMB | Grave Wolf I → Grave Wolf II → Grave Rend |
| Two normals → RMB | UpperAttack launcher |
| Three or more normals → RMB | Red Reaver → Bonesunder |
| Modifier + LMB | Direct UpperAttack launcher |
| Modifier + RMB | Full Grave Wolf |
| Dodge recovery → RMB | Iron Gale → Molten Arc |
| Dodge recovery → modifier + RMB | Mooncleaver |
| Sprint for at least 0.6s → LMB | Mooncleaver Rush |
| Ultimate input with full meter | Last Eclipse → rage |

The full Grave Wolf animation gets its own entry instead of replaying the short cuts at the end of their chain. Mooncleaver becomes its authored stationary rising/falling strike; UpperAttack owns launching.

All fifteen existing Big Sword entries remain accessible, with Last Eclipse classified as the ultimate. LMB after a grounded technique starts a fresh normal combo. No nearby staggered or airborne enemy silently replaces the requested route.

### Preserve the spinning combo feel

- Treat Tide Splitter → Ashen Cleave → Stonebreaker and dodge → Iron Gale → Molten Arc as first-class acceptance sequences, alongside normal attacks mixed between techniques.
- Preserve the full authored rotations, acrobatic body poses and useful root travel. Do not shorten a spin to a single swipe or blend away its last damaging pass.
- Keep the current fast playback as the initial baseline for spinning/sweeping moves. Reserve deliberate Maliketh-style anticipation for selected heavy finishers, waves and Last Eclipse; do not add a charge pause to every technique.
- Blend directly into a requested follow-up after the final contact plus the shared transition delay. Do not insert an idle stance, sheathe/draw cycle or mandatory movement reset between valid links.
- Keep the spectral sword manifested across connected Big Sword techniques. End each move's trail gate and clear its sampling history at handoff, so weapon continuity does not draw a ribbon across unrelated poses.
- Each chain link still requires its own press, resource validation and payment. A valid queued link commits at the first allowed transition; an unaffordable or cooling-down link leaves the source intact and permits the normal/dodge exit.
- Keep enemies within useful reach during intermediate sweeps. Reserve stronger scatter or knockdown for finishers, avoiding repeated pursuit of enemies knocked out of the combo.
- Normal attacks, ground techniques and optional aerial branches must coexist. Encounters must not require launching to make ordinary crowd clearing effective.

Capture dodge context for 0.2 seconds after dodge completion. The sprint clock resets on attack, dodge, loss of sprint or displacement. An unaffordable sprint technique produces a clear denial rather than silently performing a different attack.

### Shared request and transition rules

Retain one pending request with a 0.25-second lifetime:

- Capture move, modifier, branch and intended target when pressed.
- Latest valid request replaces the pending request.
- Use gameplay time for expiry so hitstop does not consume the player’s input opportunity.
- A queued request does not spend resources or start cooldown.
- Validate animation, resources, target-dependent requirements and movement ownership before cancelling the source.
- Permit transitions after the final required contact/projectile release plus 0.06 seconds.
- Failed requests leave the source action intact.
- Flush requests on death, teleport, scene change and menu entry.
- No stale request fires when a menu closes or control returns.

Drinking is a committed action and does not retain attack requests for execution after the sip.

### Uppercut and launch ownership

Use the Big Sword UpperAttack source to author the movement and contact, with its height-neutral variant for controlled capsule movement. Review both source and retargeted Hero before authoring the final strike window.

Initial tuning:

- 24 damage, 30 poise damage, 14 stamina.
- 2.6m reach, 120° frontal contact.
- Up to three living, launch-vulnerable enemies.
- Initial lift velocity 8m/s with 14m/s² gravity.
- Maximum supported height: 3m above the launch floor.
- No invulnerability.

Primary target selection: valid locked contact first, otherwise the closest contact to the attack bearing. Additional victims are selected by distance within the actual contact volume.

A confirmed launch creates an aerial session containing the Hero and launched victims. Rise through collision-safe movement; never parent enemies to the Hero or teleport them into formation.

A miss, resistant enemy or boss still receives the authored strike where applicable, but does not grant an aerial session. The Hero completes the short hop and returns to ordinary falling/landing.

### Playable aerial combo

Use Jump_Attack_Combo_ALL as the continuity reference and Big Sword parts 1, 2 and 3 as separately controlled attacks.

- Either LMB or RMB advances the next strike.
- Simultaneous LMB/RMB produces one request.
- Each press commits one strike; holding does not continue.
- Initial damage: 18 / 20 / 26.
- Stamina: 8 / 8 / 10; no mana cost.
- Fit each part to approximately 0.4 seconds, preserving distinct anticipation and contact.
- Entry/chain blends start at 0.06 seconds.
- Derive hit windows from actual blade motion, not identical fractions.
- Each enemy can take damage once per authored strike window.

Confirmed contacts briefly arrest downward motion for up to 0.12 seconds per strike, capped at 0.36 seconds for the session. They do not repeatedly add height. Misses provide no suspension.

End support after three strikes or 2.4 seconds from successful launch, excluding global hitstop. Then gravity resumes; no infinite air chain or aerial relaunch.

The primary target guides limited correction, not a hard attachment:

- Maximum horizontal correction 2m/s.
- Stop before obstacles and overlapping capsules.
- No snapping through walls or ceilings.
- If the primary dies, transfer only to another living victim from this launch.
- Without a valid victim, finish the current motion and fall; do not acquire an unrelated airborne enemy.

Keep the spectral blade drawn through the session and its landing recovery. Restore the equipped weapon on exit, unless rage or a deliberate weapon swap now owns it.

### Aerial exits and plunge

During a launch session:

| Input | Result |
|---|---|
| LMB or RMB | Next aerial combo strike |
| Modifier + LMB | Plunge toward the floor |
| Modifier + RMB | Skyfall Edge, spiking a valid launched victim |
| No further attack | Finish the current motion and fall normally |

Outside a launch session, airborne LMB or RMB remains plunge. Skyfall requires a living session victim; a failed request preserves the current action.

Keep the established plunge preparation, animated fall and grounded recovery. Verify:

- Capsule-bottom contact against walkable geometry.
- One 32-damage, 2.6m landing slam.
- One floor impact even when no enemy is hit.
- No enemy-body suspension, sinking or repeated slam.
- Buffered combat exit after 0.16 seconds; movement after 0.24 seconds.
- FootGrounding remains the sole visual-height owner.

Skyfall’s enemy spike does not automatically grant the separate player plunge slam. Damage and feedback are owned by their respective authored events.

## 3. Combat feel, resources and moving drinks

### Movement and targeting

Add explicit aiming policies to moves:

- **Movement-led:** normal crowd cuts, spins and broad sweeps.
- **Target-led:** focused strikes and projectiles.
- **Launch-session:** uppercut continuation and aerial attacks.

Resolve facing at entry, with limited adjustment during anticipation. Freeze attack bearing at first contact, except for authored rotation such as a spin. Avoid instant turns during damaging frames.

Consume authored root travel once through the CharacterController. Do not add synthetic forward movement on top of existing root displacement. Use controlled movement only for the explicit launch/aerial system.

Maintain katana’s 2.6m reach and 180° normal contact volume. Preserve existing Big Sword damage, poise, maximum reach and cooldowns initially, except for the new uppercut/aerial attacks and ultimate resource change.

### Contacts and enemy reactions

Add explicit reaction profiles:

| Contact type | Initial response |
|---|---|
| Normal cut | 0.2m directional recoil, approximately 0.2s flinch |
| Broad sweep | Up to 0.5m lateral stagger, approximately 0.3s |
| Spin passes | Small displacement, keeping victims within subsequent passes |
| Uppercut | Shared launch session |
| Aerial strike | Brief bounded suspension and readable midair recoil |
| Spike | Downward velocity and approximately 0.7s landing recovery |
| Heavy finisher | Up to 0.8m displacement and approximately 0.6s recovery |

Respect poise and boss resistance. Return a structured damage result so reaction logic can distinguish a real poise break, resisted hit and death from the same contact.

Preserve the satisfying repeated contacts of the spin. Replace damage based solely on elapsed re-hit time with authored blade-pass windows, keeping up to four validated passes and distributing its current approximately 40 damage/36 poise across the actual pass count. Do not collapse the whole spin into one hit or invent extra contacts when the blade has not crossed the victim again. Record the existing pass cadence before migration and compare it with the revised version against the same enemy group.

Deduplicate by health owner per window/pass, enforce height and obstacle checks, and process frames that cross narrow windows. Sweep moving blades/projectiles between previous and current samples.

### Feedback and visual identity

Use different rhythms for quick cuts and signature attacks:

- Quick cut hitstop: 0.04s.
- Heavy contact: 0.07s.
- Major finisher: 0.10s.
- Plunge: retain its single 0.06s impact freeze.
- Spin: small first-contact beat and stronger final beat; every intermediate damaging pass retains readable reactions, sparks and sound without repeated global freezes. Reduced freeze must not make the spin feel weightless or remove its multi-hit rhythm.

Aggregate crowd feedback by contact event. Multiple victims do not extend freeze duration or multiply camera shake.

Big Sword presentation:

- Dark readable blade body with a narrow crimson edge and restrained pale highlights.
- Retain current 1.45× spectral scale initially; do not enlarge it further.
- Blade-following ribbon lifetime 0.10s, 0.14s for Last Eclipse, opacity capped at 0.55.
- Wisps and sparse embers follow blade motion; no broad persistent translucent panels. Preserve a readable sweeping arc throughout actual spin gates instead of stripping the effect down to isolated contact sparks.
- Heavy anticipation gathers energy, followed by a fast release. Apply this to heavy finishers and wave attacks, while spins and linked cuts retain their fast, flowing rhythm.
- Quick aerial strikes remain crisp and short.
- Shorten orange contact streaks and cap overlapping particle bursts.
- Aggregate repeated damage numbers per victim within 0.12 seconds; preserve critical and poison distinctions.

Use existing Arc Blade as the dedicated dark wave rather than adding projectiles to every technique. Preserve its 15 mana, 1.6s cooldown, 30 damage/25 poise, 18m/s speed and 12m travel. Keep its swept 2.4m × 1.6m collision shape and projectile-owned damage.

Last Eclipse receives a compact surrounding slash eruption aligned with its actual damaging window. Decorative slashes do not create extra damage ticks.

Repair unsupported materials on every spawned enemy variant and project-owned particle/trail renderer. Preserve vendor materials. Validate actual runtime instances, including corpses and pooled effects.

Inventory existing audio before wiring attack sounds. Use distinct windup, release, contact and landing cues from available licensed assets; report missing sound assets explicitly.

### Mana and earned rage

Remove passive mana regeneration. Keep health/mana flasks and checkpoint recovery.

- Trash kill: 6 mana.
- Elite kill: 12 mana.
- Rewards require player ownership and occur once per genuine death.
- Boss recovery: a successful normal melee action restores 2 mana, at most once per action and once per 0.5 seconds. Techniques, projectiles, damage-over-time and invulnerable contacts do not qualify.
- Preserve current technique mana costs and cooldowns for the first measured tuning pass.

Before accepting the removal of passive regeneration, measure how many complete spin/sweep chains the starting mana pool supports and how kill returns sustain the next engagement. Crowd economy acceptance requires a full Tide Splitter → Ashen Cleave → Stonebreaker chain from a full starting pool, plus useful normal/dodge options when depleted. Do not raise costs or add chain-specific cooldowns to discourage the spinning playstyle the player likes.

Ultimate meter:

- Capacity 100; separate from mana.
- Successful normal action: 3 points.
- Successful technique or art: 4 points.
- Trash kill: 4 additional points; elite kill: 8.
- Maximum 12 points from one action, including attributed kills.
- Damage-over-time ticks do not repeatedly award meter.
- No meter earned from Last Eclipse or during rage.
- No passive gain or decay.
- Reset on death/rest; do not persist through save loading.
- Spend 100 only when Last Eclipse commits successfully.

Last Eclipse costs no mana. Keep its world-slowing windup without camera panning, with the Hero’s animation, action clock and effects using a consistent clock. Restore time through explicit ownership on completion, cancellation and death.

After completion, grant 15 seconds of rage:

- Existing seven-hit Big Sword normals.
- Retain normal access to the spinning technique routes during rage; rage complements them rather than replacing them with normals only.
- Purposeful authored travel and stronger finisher reactions.
- Normal stamina costs and defensive vulnerability.
- No blanket movement/attack-speed multiplier.
- Timer pauses during hitstop; non-pausing menus do not pause it.
- Restore the previous weapon after the current action safely exits.
- Manual weapon swaps end rage without restoring over the player’s choice.

### Moving health and mana drinks

Create an upper-body drinking Animator layer and AvatarMask, leaving locomotion and foot planting active underneath.

- Move and strafe at 50% normal walking speed.
- No sprint, jump, attack, dodge, slide or wall-run during the sip.
- Start only while grounded and outside conflicting actions.
- Keep current 1.6s sip and restoration during its existing 35–75% swallow interval.
- Spend one charge immediately.
- No voluntary cancellation or refund.
- Preserve existing behaviour where ordinary damage does not stop drinking; no invulnerability.
- Death or loss of valid grounded support cancels the remaining restoration without refund.
- Capture flask type at start so input changes cannot change the resource mid-sip.
- Empty-flask animation permits the same slow movement and spends nothing.

Use a dedicated drinking state instead of setting or clearing global IsRooted. Hide/stow the visible weapon while the bottle occupies its hand, then restore current weapon ownership safely.

Update setup verification to expect the additional masked layer; it must not rebuild the controller back to the legacy single-layer structure.

## 4. Courtyard, camera, UI and implementation boundaries

### Complete combat slice

Create a separate project-owned courtyard scene. Do not rebuild or overwrite the existing level.

Initial layout:

- Approximately 24m × 20m usable fighting space.
- Two nearby combat pockets with a broad connecting lane.
- Two explicitly marked wall-run surfaces.
- One low platform for plunge entries and a short stair/slope section.
- Clear central floor with ruins, pillars and restrained perimeter detail.
- Existing dark-fantasy assets and lighting; no new dependencies.

Provide three repeatable encounters:

1. Three interruptible melee enemies for learning routes.
2. Four light enemies and one poise-bearing elite.
3. Eight-enemy stress test, disabled by default.

For standard encounters, allow at most two simultaneous melee commitments, with staggered strike timings. Waiting enemies reposition visibly rather than stand on top of each other. Launch, stagger, death and disengagement release attack reservations.

Wall exits return to ordinary airborne controls, so attacks plunge unless a launch session exists. Do not grant air-combo permission simply for leaving a wall.

### Camera and readability

- Keep manual camera control; no forced ultimate orbit.
- Frame the Hero and primary launch victim together using a clamped vertical focus adjustment.
- Smooth the return to ground framing.
- Ensure airborne targets near ceilings do not drive the camera into geometry.
- Keep attack FOV independent of slide displacement.
- Cap crowd shake and avoid repeated shake per spin victim.
- Test combat framing against walls, stairs and five-enemy groups.

Keep the three Q-art sockets and existing Persona HUD. Add:

- A compact earned-meter indicator near the ability area.
- A contextual command hint while the modifier is held.
- A small indication of the next aerial strike and available finisher.
- A controls-page route chart using current bindings.

Do not restore floating enemy health bars.

### Code and data structure

Introduce explicit move metadata for action family, aiming policy, reaction profile and aerial eligibility. Visual theme must not determine whether a move is a technique or ultimate.

Add:

- A combat input router.
- A bounded aerial-session owner.
- A dedicated ultimate resource component.
- Structured contact results for damage, poise break and kill attribution.
- Drinking movement restriction and upper-body animation ownership.

Keep AttackController as the action executor; avoid creating competing movement/gravity owners. Continue using the shared blade pose resolver for runtime and preview.

Update assets in place. Setup migrations must be versioned and repeatable, preserve unrelated arts, and map saved art selections by stable asset identity rather than shifted list positions.

Update this combat planning document during implementation with the final routes, tuning, migrations and acceptance results.

## 5. Delivery and acceptance

Implement in this order:

1. Record the current spinning/sweeping chains as the feel baseline, then preserve them through input ownership and explicit action metadata changes; resolve controller conflicts.
2. Polish grounded technique chains, normal/technique/dodge handoffs, blade continuity and intermediate enemy reactions.
3. Add uppercut, launch session and three-step aerial combo as optional branches, then complete the full technique route map.
4. Refine authored spin passes and aggregated feedback against the baseline; preserve the repeated-contact rhythm and full rotations.
5. Moving drinks and controller-layer compatibility.
6. Mana economy, ultimate meter and rage.
7. Materials, blade effects, audio and camera.
8. Courtyard, encounter coordination and complete validation.

Compile both C# projects using dotnet build. Add new scripts to project files until Unity regenerates them.

Automated/focused checks must cover:

- Modifier precedence, simultaneous presses, held inputs and expired requests.
- One payment/cooldown per committed action; failed transitions preserve the source.
- All fifteen techniques reachable through the documented routes.
- Neutral and sprinting RMB both preserve access to Tide Splitter; unrelated airborne enemies cannot hijack it.
- E → B → D and F → H follow-ups commit without an intervening idle or sword respawn; each move pays once.
- Spin damage remains once per victim per validated blade pass, with no contact lost to a recovery transition.
- No full Grave Wolf replay after the short sequence.
- Uppercut miss, resistant enemy, poise break, three-target cap and boss contact.
- No air combo after an ordinary jump.
- Three-strike limit, bounded suspension, primary-target death and ceiling/wall collision.
- One plunge impact, including empty-floor landings.
- Damage deduplication, crossed contact windows and projectile obstruction.
- Ultimate meter caps, rage exclusion, time restoration and manual weapon swaps.
- Immediate flask charge consumption, partial restoration on death/fall, slow movement and blocked combat actions.
- Repeatable setup, valid materials and preserved art attunements.

Live acceptance must separately cover:

- Every Big Sword technique plus uppercut and all three aerial clips on Hero and source rig.
- Correct grip and effects with katana and Big Sword equipped.
- Keyboard/mouse and controller routes.
- Three- and five-enemy fights.
- Side-by-side before/after captures of Tide Splitter → Ashen Cleave → Stonebreaker and dodge → Iron Gale → Molten Arc against the same group: retain full rotations, multi-hit rhythm, quick links and readable blade arcs.
- Complete a standard crowd encounter using grounded normals, spinning techniques and dodges only; launching and the ultimate must be optional.
- Verify early normal/dodge exits after the final contact and continued RMB chaining, without forced idle poses or unnecessary sword disappearance.
- Launch near walls, stairs, slopes, ceilings and platform edges.
- Plunge from rising, falling, low and high positions.
- Drinking while walking forward, backward and strafing.
- Boss mana sustainability and immunity to inappropriate launch reactions.
- Clear silhouettes, distinct heavy/quick rhythms and no magenta enemies or lingering effects.

Run manual Unity setup only after import finishes and outside Play Mode. Do not launch a second Unity instance. Report compile/static results separately from live visual and device checks; the user is not responsible for authoring contact windows.
