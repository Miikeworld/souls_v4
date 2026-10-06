# Fast crowd combat implementation — 1 October 2026

The former charge prototype is replaced. Normal attacks commit on LMB/RT press; holding produces no extra attack. An airborne press selects plunge. Q/LB and three existing selectable/attunable slots are unchanged.

All fifteen Big Sword assets retain their existing GUIDs, names, icons and library order. Costs are ceil(75% of the old table). Playback is multiplied by the requested family multiplier, with 1.2/1.8/2.2s cooldowns. Damage, poise and maximum reach stay unchanged. Arc Blade is the 15-mana, 1.6s cooldown, 30 damage/25 poise, 18m/s piercing dark wave, 12m maximum travel, swept 2.4m x 1.6m collision; no duplicate melee damage.

## Apply manually in Unity

After import/compile completes, outside Play Mode, run **Tools > Project Restart > Setup Fast Crowd Combat**, then save the open scene. This updates the existing art states and tuning, removes charge states, creates project-owned Polygon Particles variants and applies combat camera framing. It does not rebuild the level. Both this helper and normal combat setup preserve existing library indices/attunements and unrelated arts; they append missing entries.

**Review Big Sword Contacts (Hero + Shadow)** is an optional animation viewer: Hero on the left, original source on the right. Pick a skill and enable playback. Timing editing is hidden under Advanced and is not a required user step. **Check All Big Sword Blade Poses** samples all fifteen clips at 61 positions each and reports rotation, palm attachment and blade size. These measurements verify mapping, not live contact timing or collision-safe travel.

The implementation pass analyzed all fifteen source FBX trajectories and skeleton poses, then replaced the old late timing fractions with individual motion-derived hit/trail/impact gates. Grave Wolf full chain has two separate contacts; Mooncleaver variants show the rise and fall but damage only on the final strike. Tide Splitter covers the authored spin. Bonesunder's source motion is a horizontal heavy cut, so it keeps contact feedback without a fabricated floor scar. Setup persists these source-derived defaults while retaining explicitly reviewed timing edits. `contactMotionReviewed` remains false until actual Hero/live visual acceptance; source analysis does not silently certify retargeted gameplay.

## Runtime behavior

A single captured request lasts 0.25 seconds of real time. Latest request replaces it. Combo/art/dodge transfers after final damage or projectile release + 0.06s. Destination animation/resource/cooldown checks run before releasing the source; failed requests consume no mana/cooldown and preserve the source action. Normal after art starts a fresh combo. Normal costs retain the existing stamina economy.

Katana keeps 2.6m range and now has a 180-degree frontal volume. Movement chooses entry facing; a locked bearing is the neutral fallback. Root travel uses the CharacterController; scripted target pull is removed. Contact windows detect skipped-frame crossings, deduplicate Health owners separately per window, and reject vertically separated or obstructed targets. Crowd feedback aggregates per frame without extending an existing owned freeze.

BladePoseResolver maps source weapon rotation through the Hero's frame and anchors it at the palm. Runtime and preview share this mapping. The socket samples actual outgoing/incoming Animator state times, without an additional rotation filter. Summoned Big Sword arts use ActiveBladeSet, preserving Big Sword calibration when a katana is equipped. RightHand is HumanBodyBones value 18.

Dark blade ribbons sample the final weapon socket pose at execution order 100 and emit only within trail gates. Endpoints come from visible blade geometry, excluding the handle. Ribbons have a narrow crimson rim, alpha at most 0.55 and lifetime at most 0.10 seconds (0.14 for Last Eclipse). Fire edge/sparse smoke/contact sparks are project-owned Polygon Particles variants. Heavy strikes add brief ground contacts; sweeps use the actual blade ribbon. Cancellation, death and weapon changes clear attached effects; large teleports clear sampling history. Vendor materials/assets are preserved. Enemy blue hit slices are removed from runtime and setup.

## Plunge flow

An airborne attack validates preparation/fall/landing animations and stamina before committing once. AirDrop_Start enters over 0.12 seconds and lasts 0.35 seconds. Inherited vertical velocity brakes over 0.08 seconds, followed by an air hang. Falling enters over 0.08 seconds, accelerates to -18m/s over 0.18 seconds and permits at most 1m/s camera-relative steering. There is no target pull, character pitch or time limit on a high drop.

Manual setup creates the 0.4-second fall loop from AirDrop_Keep's body/grip pose and half-strength CLazy falling leg motion, with a closed seam and no root translation. CLazy clips keep their native avatar; Grruzam avatar baking is restricted to that pack.

A walkable capsule-bottom contact triggers one 32-damage, 2.6m slam with owner deduplication, height and obstruction checks. Empty-floor landings also produce a surface-aligned impact lasting at most 0.6 seconds, one 0.06-second freeze and 0.25–0.35 shake. AirDrop_End plays at 1.2 speed with a 0.06-second blend. Buffered combo/skill/dodge becomes eligible at 0.16 seconds and movement at 0.24 seconds. Grounding keeps sole-height ownership through recovery; genuine loss of support returns to ordinary falling without repeating the impact.

## Verification

Run scripts with PowerShell 7. `Tools/Test-CrowdCombat.ps1` uses actual RecoveryBuffer/CombatContactClock/PlayerMana source for expiry, replacement, capture, payment and skipped-window checks. `Tools/Test-BladePose.ps1` checks actual resolver frame mapping, offsets, palm anchoring, scale and crossfade endpoints in an isolated numerics host. `Tools/Test-Plunge.ps1` checks actual preparation/descent velocity and walkable-normal rules. These scripts do not execute Unity animation or physics.

Both C# projects are built with dotnet. In Unity run **Run Fast Crowd Combat Checks** for focused input/resource/pose/material/data checks; the log reports how many clips have been marked reviewed.

Still required: preview all fifteen Hero/shadow clips, review grip/ribbons/contact/impact cues/root travel, then keyboard/mouse and controller group playtests with 3–5 enemies. Check combo→every art→combo/dodge, exact payment/cooldowns, separate full-chain windows, duplicate colliders, height separation, thin walls, no stacked freezes, cancellation/death/swap/teleport cleanup and no enemy blue slices. Rerun setup and verify library indices, GUIDs and attunements remain unchanged. No live Unity acceptance was performed here.
