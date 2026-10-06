# The Foundry Walk — tutorial direction

The tutorial is a separate scene, not a replacement for the ruin level. It follows a ruined maintenance route into a small forge courtyard. Keep the camera corridor open, repeat one masonry kit and one bronze trim kit, and leave broad clear floors around teaching encounters. Props sit at the perimeter; they do not conceal movement markers or crowd the player silhouette.

## Teaching contract

Every mandatory lesson has: a safe introduction, practice with one instruction, an observed action, then a short transfer test. Entering a volume or pressing a key alone is not proof of understanding. Only committed actions, successful traversal and actual enemy outcomes count. Failed traversal returns to a nearby safe platform. Combat can be retried without replaying completed movement lessons. Never trap the player because a consumable or mana pool was exhausted.

Prompts follow the live control scheme. Default exploration is Ctrl toggle sprint, Space jump, Shift slide/crouch; the alternate scheme uses Shift hold sprint, F jump, Space slide. Combat dodge uses its actual binding, not the exploration slide binding. Controller hints must come from the same control source. Do not describe Ctrl as hold-sprint in the default scheme.

## Sequence

1. **Arrival / movement.** Walk toward a copper-lit doorway, sprint on the straight approach, then jump a low sill. No enemies. Teach stamina as an expend-and-recover resource. Transfer: reach the next landing using jump; no fatal pit.
2. **Momentum.** A low beam on a clear straightaway teaches sprint → slide. A generous second landing teaches a second jump in the air. Verify a real slide and double-jump burst, not raw button presses. The practice floor remains underneath both.
3. **Wall route.** A single marked wall beside a safe floor introduces airborne automatic attach. Press jump to push off. Next, two offset marked walls teach alternating sides. Unmarked walls remain visually quieter and do not accept wall runs. Transfer: reach a raised landing after an actual wall-run; catching a rail or merely walking around cannot count.
4. **Blade basics.** One isolated skeleton, a wide arena and a rest point. Explain attack, lock-on, dodge and stamina recovery. Transfer: defeat the skeleton. A successful dodge action is a practice goal, not proof that an attack was evaded.
5. **Rear critical.** A guard faces away from the entrance. Clear space behind it allows the player to approach to the rear-attack distance without entering its frontal sight cone. Explain rear position + armed normal attack. This is a backstab, not a promised crouch/invisibility/noise mechanic. Transfer: land a backstab on the guard. A critical is not necessarily a one-hit kill; don't require death unless its health is deliberately balanced for one.
6. **Big-sword techniques.** The starting weapon remains katana. RMB/RB summons the big-sword technique; it does not require inventing a weapon-switch input. Teach neutral technique, then the first-normal branch, then two committed normals → technique launcher. Presses queued early do not count as completed normals. Use a sturdy practice victim with no poise meter, so the launcher can succeed without first introducing posture. Refund practice mana between attempts; never alter the global weapon assets.
7. **Aerial session.** After a successful launcher, either attack input advances the three air strikes. The chase/spike route requires a living session victim. Transfer: launch and connect an air follow-up. A failed launch hop is not success. An ordinary jump near an airborne enemy does not create permission for an aerial chain.
8. **Plunge.** A low reachable ledge overlooks two enemies. Airborne normal attack starts the plunge; completion requires touchdown/contact and an enemy damaged by the action. Keep the floor clearly visible and the landing zone spacious.
9. **Checkpoint / resources.** Light then rest at a Checkpoint; explain healing, flask refill, enemy reset and the saved return point. Teach flask use before presenting lethal pressure. Level-up and attunement are optional side lessons; the tutorial does not force a permanent stat purchase or modify the saved character file just to demonstrate menus.
10. **Transfer courtyard.** No new controls. A marked wall route offers a flank, a rear-facing guard rewards positioning, and a group rewards area techniques or a plunge. Clear the encounter and reach the exit. Multiple approaches are valid. Record which learned mechanics were used; offer a practice reminder for unused techniques rather than force an arbitrary combo on the final enemy.

## Optional advanced annex

Sprint continuously for at least 0.6 seconds → normal attack for the dash strike. Ultimate uses T / D-pad up and earned charge; rage is not invulnerability. Introduce it after enough real combat has built the meter; never silently seed a full permanent meter. Q arts and attunement, item use, posture on elites, death / dropped souls recovery, and an optional wall-chain challenge belong here after the essential path. They are available mechanics, not all mandatory gates.

## Visual hierarchy

- Architecture: charcoal ink on actual creases; no diagonal lines across coplanar triangles.
- Navigation: tarnished copper frames and a repeatable wall-run marker.
- Traversal: violet energy, matching the player's existing effects.
- Combat: crimson technique accents, with the katana's purple kept distinct.
- Foliage: low contrast, sparse perimeter placement; don't draw every leaf triangle.
- Distance: structural lines fade to atmospheric silhouettes, avoiding a tangled horizon.

## Acceptance

Compile, inspect shader errors, test saved-scene reload, review at gameplay camera distance, and walk the playable route. Check wall attachment on the marked walls, success and retry conditions, live control-scheme prompts, enemy reset, resource exhaustion, death and checkpoint return. A static scene inspection does not certify traversal, hit contacts, controller input or tutorial completion.

## Current playable blockout

`02_FoundryTutorial.unity` implements ten sequential 20m bays, with a safe floor under traversal. The wall lesson requires actual attachment, wall-jump burst and arrival; its landing is currently flat. Rear critical, technique, air follow-up and plunge lessons require contacts on their designated targets. Recovery observes a spent flask charge and a real rest before arrival. The final yard has three guards, a marked wall flank and a low plunge deck; reaching the exit after defeating the guards opens a return-to-menu prompt.

The first visual Play Mode review found the starting rear wall obstructed the camera. It was moved 10m behind the entrance and the entry floor extended. Floor panels add a consistent architectural rhythm. Instruction cards show keyboard and controller bindings, with keyboard movement bindings following the saved control scheme.

This is an authored teaching blockout. A full route playthrough, controller operation, landing/foot-sliding acceptance, failure/retry and death/reset acceptance, encounter difficulty and sound mix are still unverified. Existing Unity inspector reload errors are not shader compilation failures. Do not report this as a finished, certified tutorial level.

## Dark Fantasy presentation

The tutorial now uses the existing Dark Fantasy masonry kit: square floor modules, repeated wall panels, gothic arches, pillars, actual iron gate doors, flags and braziers. Weapon racks identify practice areas; restrained bookshelf and crate groups sit at the edges. Keep the central teaching space clear. Most modules retain uniform scale and native proportions; obstacle skins fit the original teaching collision. Source vendor prefabs, meshes and materials remain unchanged.

`Tools > Project Restart > Tutorial Art > Dress Foundry with Dark Fantasy` refreshes only the generated presentation over the loaded tutorial. It saves a timestamped scene backup first, retains the ten lesson/gate references and marked wall colliders, then reapplies the mechanical crease materials. The full tutorial builder also calls this pass. Run manually outside Play Mode. The atmospheric pass uses cool stone, blue-grey distance fog and warm local brazier lights. A broad KillZone below the course routes escaped falls through the existing death/Checkpoint flow.

`Capture Ten Room Views` renders ten fixed world cameras to `Tools/TutorialReview`; these are composition checks, not evidence of live lesson completion. Live route, collision and retry acceptance still applies.

Presentation verification (2026-10-05): runtime and editor dotnet builds pass with zero errors/warnings. Ten room renders reviewed; gate scale corrected after the first pass and local-light support fixed for the active clustered URP renderer. Final live entrance camera checked in Play Mode, then editor returned outside Play Mode. No shader error was found in the Editor log review. This does not certify the full lesson route or FallReset/death transaction.

Guidance revision: Foundry lessons use one 48px-high contextual input strip, briefly displayed and then recalled as a quiet reminder. A pulsing copper ground marker indicates the current practice location (rear of guard, wall approach, stair foot, Checkpoint or landing). Action progress changes the cue; opening gates replace per-lesson completion banners. Final assessment leaves the approach to the player. The momentum lesson has a copper-chevron runway and a beam raised to 4m, because slide currently retains the standing capsule; it teaches slide momentum, not clearance under low obstacles. Full reset rebuilds preserve this layout.

The compact hint's size and fade/reminder were reviewed in the live entrance camera. Full input-driven phase transitions and the complete slide/jump sequence still need route acceptance; automated key taps did not establish an observed successful jump. The first marker is on the sill approach, so the sill does not hide it.

Final entrance review confirmed the copper approach ring is visible before the sill and the large instruction card is absent. Saved slide beam bottom is 3.75m; six directional floor-arrow strokes are present. Runtime and editor compilation pass with zero warnings/errors.
