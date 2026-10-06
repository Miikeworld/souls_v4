using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden's summoned-weapon attacks — the recurring motif that escalates:
///   P2  Crown of Blades: six weapons form behind his shoulders like a crown
///       (his empty hand raised), then launch on a rhythm. Below crownTenAt the
///       crown is TEN, mixed swords, axes and greatswords, faster.
///   P2  Reaper's Wheel: axes stood on edge rev up at his side and roll down
///       marked lanes — jump them or leave the lane.
///   P2/P3 Impaler's Ring: spears form in a ring around where you stand and thrust
///       through the centre in opposite pairs — leave the ring or time the dodge.
///   P2  Crimson Cyclone: his own greatsword, a travelling spin (wound-up first).
///   P3  Crimson Guillotine: blade straight overhead, a colossal spectral axe
///       hangs over a marked strip; it drops, he leaps the same line slamming —
///       a miss wedges his sword in the stone (the punish).
///   P3  Armory of the Fallen: weapons ripped OUT of the arena floor circle him,
///       then fire one by one in a spiral.
/// Every weapon is a Synty mesh drawn as a crimson ghost (WardenArsenal) with the
/// fight's read: form → glow → path flash → fire.
/// </summary>
public sealed partial class BossLord
{
    // ================================================================== Crown of Blades (six → ten)

    /// <summary>He stops, raises his empty hand and lifts half a metre; the crown forms
    /// behind him one chime at a time, then fires on a rhythm — six: 1 · 2 · 3-4 · 5 · … 6
    /// (the late sixth punishes six panic rolls); ten: pairs that tighten. Then he drops, heavily.</summary>
    private IEnumerator CrownOfBlades()
    {
        var ten = health.Current <= health.Max * crownTenAt;
        var count = ten ? 10 : 6;
        Play(CrownRaiseId, 0.2f, 1f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.35f);
        yield return Wait(0.35f, 200f);
        Play(CrownHoldId, 0.25f, 1f);
        body.SetCore(WardenBody.CoreMode.Glimmer);
        body.Charge(0.35f);
        WardenFx.Dust(transform.position, 6, 0.6f, 0.8f, null, 1.8f);
        WardenFx.Shards(transform.position + Vector3.up * 0.1f, 8, 0.6f, WardenFx.StoneCol, true, 1.8f, 2.2f, null, 1.2f);
        if (ten) cam?.Frame(3f, 0.12f, 0.5f, 6f, 0.6f, 1f);
        yield return LiftTo(0.6f, 0.6f);

        var blades = new SpectralBlade[count];
        for (var i = 0; i < blades.Length; i++)
        {
            var kind = !ten ? ArsenalKind.Sword : i % 3 == 1 ? ArsenalKind.Axe : i % 5 == 4 ? ArsenalKind.Greatsword : ArsenalKind.Sword;
            var len = WardenArsenal.NaturalLength(kind, ten ? 0.8f : 1f);
            blades[i] = SpectralBlade.Spawn(kind, -1, len, CrownSlot(i, count), CrownTilt(i, count), transform, 0.2f, true, 0.9f + i * (ten ? 0.055f : 0.09f));
            var t = 0f;
            var gap = ten ? 0.15f : 0.22f;
            while (t < gap) { t += Time.deltaTime; HoldCrown(blades, -1); yield return null; }
        }
        var lull = 0f;
        while (lull < 0.45f) { lull += Time.deltaTime; HoldCrown(blades, -1); yield return null; }

        float[] six = { 0f, 0.75f, 0.75f, 0.22f, 0.75f, 1.25f };
        float[] tens = { 0f, 0.5f, 0.2f, 0.5f, 0.2f, 0.45f, 0.18f, 0.45f, 0.18f, 1.0f };
        var gaps = ten ? tens : six;
        var tell = ten ? 0.24f : 0.3f;
        for (var i = 0; i < blades.Length; i++)
        {
            var t = 0f;
            while (t < gaps[i]) { t += Time.deltaTime; HoldCrown(blades, -1); yield return null; }
            // Tell: the weapon turns to you and swells; its path flashes on the floor.
            var b = blades[i];
            t = 0f;
            while (t < tell && b != null)
            {
                t += Time.deltaTime;
                HoldCrown(blades, i);
                b.TurnToward(WardenHazard.Chest, 900f);
                b.SetGlow(t / tell);
                yield return null;
            }
            if (b == null) continue;
            var aim = WardenHazard.Chest;
            var dir = (aim - b.Tip).normalized;
            var g0 = FloorPoint(b.Tip);
            WardenFx.Line(new[] { g0 + Vector3.up * 0.05f, FloorPoint(aim) + FlatDir(dir) * 4f + Vector3.up * 0.05f },
                          WardenFx.Crimson, 0.05f, 0.35f, 0.05f, 0.5f);
            b.Fire(dir, ten ? 33f : 30f, 16f, 34f, b.Kind == ArsenalKind.Axe ? 900f : 0f);
        }
        yield return Wait(0.3f);
        pose?.Clear(0.3f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Descend(0.45f);
        body.Charge(0f);
        // Punish window: he settles from the hover.
        yield return Wait(ten ? 1.6f : 1.3f);
    }

    private Vector3 CrownSlot(int i, int count)
    {
        var fwd = FlatDir(transform.forward);
        var right = Vector3.Cross(Vector3.up, fwd);
        var span = count > 6 ? 88f : 70f;
        var a = Mathf.Lerp(-span, span, i / (float)(count - 1)) * Mathf.Deg2Rad;
        var centre = Chest + Vector3.up * 0.75f - fwd * 0.55f;
        var r = count > 6 ? 1.95f : 1.5f;
        return centre + (right * Mathf.Sin(a) + Vector3.up * Mathf.Cos(a)) * r;
    }

    private Quaternion CrownTilt(int i, int count)
    {
        var fwd = FlatDir(transform.forward);
        var right = Vector3.Cross(Vector3.up, fwd);
        var span = count > 6 ? 88f : 70f;
        var a = Mathf.Lerp(-span, span, i / (float)(count - 1)) * Mathf.Deg2Rad;
        return Quaternion.FromToRotation(Vector3.up, (right * Mathf.Sin(a) + Vector3.up * Mathf.Cos(a)).normalized);
    }

    /// <summary>Unfired crown weapons ride their slots while he slowly turns.</summary>
    private void HoldCrown(SpectralBlade[] blades, int aiming)
    {
        Face(ToPlayerFlat, Time.deltaTime, 90f);
        for (var j = 0; j < blades.Length; j++)
        {
            var b = blades[j];
            if (b == null || b.State == SpectralBlade.Phase.Flying || b.State == SpectralBlade.Phase.Stuck
                || b.State == SpectralBlade.Phase.Dissolving) continue;
            b.transform.position = Vector3.Lerp(b.transform.position, CrownSlot(j, blades.Length), 1f - Mathf.Exp(-14f * Time.deltaTime));
            if (j != aiming) b.transform.rotation = Quaternion.Slerp(b.transform.rotation, CrownTilt(j, blades.Length), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }
    }

    // ================================================================== Reaper's Wheel

    /// <summary>His hand sweeps low; axes stood on their edges form at his side and rev
    /// (sparks spitting off the stone) while their lanes show — then they roll, one
    /// after another. Jump each (about a body high) or step out of its lane.</summary>
    private IEnumerator ReapersWheel()
    {
        Play(CrownRaiseId, 0.2f, 1f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.3f);
        yield return Wait(0.3f, 220f);
        Play(CrownHoldId, 0.25f, 1f);
        body.Charge(0.4f);
        var late = health.Current <= health.Max * crownTenAt;
        var count = late ? 5 : 3;
        var fwd = ToPlayerFlat;
        var right = Vector3.Cross(Vector3.up, fwd);
        var origin = FloorPoint(transform.position);
        var spread = late ? 20f : 26f;
        for (var i = 0; i < count; i++)
        {
            var ang = (i - (count - 1) * 0.5f) * spread;
            var dir = Quaternion.AngleAxis(ang, Vector3.up) * fwd;
            var start = origin + dir * 1.8f + right * (i - (count - 1) * 0.5f) * 0.4f;
            var lane = Sanctum != null ? Mathf.Clamp(CrackLength(start, dir) + 1f, 8f, 26f) : 22f;
            var kind = i % 2 == 1 && WardenArsenal.Has(ArsenalKind.Scythe) ? ArsenalKind.Scythe : ArsenalKind.Axe;
            AxeWheel.Spawn(start, dir, lane, 0.95f + i * 0.3f, late ? 14f : 12f, 20f, transform, kind, 1.1f);
            yield return Wait(0.1f, 60f);
        }
        WardenAudio.Play("scrape", origin, 0.5f, 1.1f);
        yield return Wait(1.2f + count * 0.3f, 40f);
        body.Charge(0f);
        pose?.Clear(0.3f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Wait(1.0f);
    }

    // ================================================================== Impaler's Ring

    /// <summary>He points; spears form in a ring around where you stand, aimed at
    /// the centre, each with its line across the floor. They thrust through in
    /// opposite pairs — leave the ring between two spears, or time the dodge.</summary>
    private IEnumerator ImpalersRing()
    {
        Play(CrownRaiseId, 0.2f, 1f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.25f);
        yield return Wait(0.25f, 260f);
        Play(CrownHoldId, 0.25f, 1f);
        var count = phase == 2 ? 8 : 6;
        const float radius = 5.2f, height = 1.25f;
        var centre = FloorPoint(WardenHazard.Feet);
        var centreHigh = centre + Vector3.up * height;
        var spears = new SpectralBlade[count];
        var marks = new WardenMark[count];
        var dirs = new Vector3[count];
        var offset = Random.value * 360f;
        var len = WardenArsenal.NaturalLength(ArsenalKind.Spear, 0.9f);
        for (var i = 0; i < count; i++)
        {
            var a = (offset + i * 360f / count) * Mathf.Deg2Rad;
            var ring = centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            var inward = (centreHigh - (ring + Vector3.up * height)).normalized;
            dirs[i] = inward;
            var grip = ring + Vector3.up * height - inward * len * 0.6f;
            spears[i] = SpectralBlade.Spawn(ArsenalKind.Spear, -1, len, grip, Quaternion.FromToRotation(Vector3.up, inward), transform,
                                            0.22f, true, 1.1f + i * 0.04f);
            marks[i] = WardenMark.Line(ring, ring + FlatDir(inward) * radius * 2f, 0.32f, WardenFx.Crimson, 0.04f, 0.08f);
            marks[i].SetAlpha(0.5f);
            yield return Wait(0.07f, 90f);
        }
        // The ring tightens a step while it gathers.
        var t = 0f;
        while (t < 0.55f)
        {
            t += Time.deltaTime;
            for (var i = 0; i < count; i++)
                if (spears[i] != null && spears[i].State != SpectralBlade.Phase.Flying)
                    spears[i].transform.position += dirs[i] * 0.6f * Time.deltaTime;
            yield return null;
        }
        var pairs = count / 2;
        for (var p = 0; p < pairs; p++)
        {
            var a = p % 2 == 0 ? p / 2 : pairs - 1 - p / 2;
            var b = a + pairs;
            t = 0f;
            while (t < 0.24f)
            {
                t += Time.deltaTime;
                if (spears[a] != null) spears[a].SetGlow(t / 0.24f);
                if (spears[b] != null) spears[b].SetGlow(t / 0.24f);
                marks[a]?.SetPulse(t / 0.24f);
                marks[b]?.SetPulse(t / 0.24f);
                yield return null;
            }
            if (spears[a] != null) spears[a].Fire(dirs[a], 34f, 18f, radius * 2f + 2f);
            if (spears[b] != null) spears[b].Fire(dirs[b], 34f, 18f, radius * 2f + 2f);
            marks[a]?.Release(0.15f);
            marks[b]?.Release(0.15f);
            WardenAudio.Play("swish", centreHigh, 0.7f, 1.3f);
            yield return Wait(0.3f, 60f);
        }
        pose?.Clear(0.3f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Wait(1.1f);
    }

    // ================================================================== Crimson Cyclone

    /// <summary>He winds up — torso coiled away, blade low behind him, a ring showing
    /// his reach — then spins, travelling at you, the greatsword cutting crescents
    /// through the air and sparks off the stone. Back out of the ring or get over it;
    /// he staggers out of the spin dizzy (punish).</summary>
    private IEnumerator CrimsonCyclone()
    {
        var id = Play(CycloneId, 0.15f, 0.9f);
        pose?.Set(WardenPose.Kind.Coil, 0.3f);
        var t = 0f;
        while (t < 1.2f && StateTime(id) < 0.11f)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 300f);
            blade.Heat = Mathf.Clamp01(t / 0.5f);
            yield return null;
        }
        AnimSpeed(0.05f);
        var reach = (blade.HasBlade ? blade.Length : 1.4f) + 1.4f;
        var ring = WardenMark.Circle(FloorPoint(transform.position), reach, WardenFx.Crimson, 0.07f, 0.05f, 36);
        WardenAudio.Play("armour", Chest, 0.45f, 0.95f);
        t = 0f;
        while (t < 0.4f)
        {
            t += Time.deltaTime;
            ring.SetCircle(FloorPoint(transform.position), reach);
            ring.SetPulse(t / 0.4f);
            blade.Heat = 1f;
            Face(ToPlayerFlat, Time.deltaTime, 120f);
            yield return null;
        }
        pose?.Clear(0.08f);
        AnimSpeed(1f);
        blade.Glint(1.2f);
        blade.NewSwing(1.4f);
        blade.Swinging = true;
        seqRootMotion = true;
        var nextHit = 0f;
        var nextCrescent = 0f;
        t = 0f;
        while (t < 4f)
        {
            var nt = StateTime(id);
            if (nt < 0f ? t >= 2f : nt >= 0.66f) break;
            t += Time.deltaTime;
            var c = FloorPoint(transform.position);
            ring.SetCircle(c, reach);
            if (PlayerDistance > 1.6f) MoveFlat(ToPlayerFlat * 2.1f * Time.deltaTime);
            if (Time.time >= nextCrescent)
            {
                nextCrescent = Time.time + 0.2f;
                var from = blade.HasBlade ? FlatDir(blade.Tip - transform.position) : FlatDir(transform.forward);
                WardenFx.Crescent(c + Vector3.up * 1.0f, Vector3.up, from, reach - 0.6f, 120f, WardenFx.Crimson, 0.1f, 0.22f, 0.05f);
            }
            if (Time.time >= nextHit && (blade.SweepHits(0.65f) || !blade.HasBlade && PlayerDistance <= reach))
            {
                nextHit = Time.time + 0.3f;
                if (WardenHazard.Damage(12f, transform.position)) Connect(12f);
            }
            yield return null;
        }
        seqRootMotion = false;
        blade.Swinging = false;
        blade.Heat = 0f;
        ring.Release(0.2f);
        // Dizzy out of the spin.
        exposedUntil = Time.time + 1.1f;
        yield return Until(id, 0.95f, 1.5f);
    }

    // ================================================================== Crimson Guillotine

    /// <summary>Blade straight overhead; above the line he faces, a colossal spectral
    /// axe forms and hangs, swelling, over a crimson strip — the room goes quiet.
    /// It drops; he leaps the same line and slams. If he misses, his sword is wedged
    /// in the stone and he has to wrench it free — the punish.</summary>
    private IEnumerator CrimsonGuillotine()
    {
        var id = Play(GuillotineId, 0.2f, 0.9f);
        pose?.Set(WardenPose.Kind.Overhead, 0.3f);
        var t = 0f;
        while (t < 0.9f && StateTime(id) < 0.1f)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 220f);
            yield return null;
        }
        AnimSpeed(0.03f);
        t = 0f;
        while (t < 0.25f) { t += Time.deltaTime; Face(ToPlayerFlat, Time.deltaTime, 160f); yield return null; }
        WardenAudio.Duck(1.6f, 0.03f);
        WardenAudio.Play("hum", Chest, 0.9f, 1.2f);
        WardenAudio.Play("armour", Chest, 0.55f, 0.85f);
        cam?.Frame(5f, 0.22f, 1f, 3.6f, 0.5f, 0.9f);
        var dir = FlatDir(transform.forward);
        var start = FloorPoint(transform.position) + dir * 1.4f;
        var extent = Sanctum != null ? Mathf.Clamp(CrackLength(start, dir), 6f, 14f) : 13f;
        var drop = GuillotineDrop.Spawn(start, dir, extent, 1.15f, 2.4f, 40f, transform, ArsenalKind.Axe);
        t = 0f;
        while (t < 1.0f) { t += Time.deltaTime; blade.Heat = t; body.Charge(t * 0.6f); yield return null; }
        body.Charge(0f);
        pose?.Clear(0.06f);
        WardenAudio.Unduck();
        AnimSpeed(1.1f);
        seqRootMotion = true;
        yield return Swing(id, 0.385f, 0.47f, 34f, 0.7f, 2.5f, 1.9f, 0.4f, 0.08f, timeout: 1.8f);
        seqRootMotion = false;
        var wait = 0f;
        while (drop != null && !drop.Landed && wait < 0.8f) { wait += Time.deltaTime; yield return null; }
        if (!lastSwingHit)
            yield return Stuck(2.2f, blade.FloorContactThisSwing ? blade.LastFloorContact : FrontPoint(2.2f));
        AnimSpeed(1f);
        yield return Until(id, 0.95f, 1.6f);
    }

    // ================================================================== Armory of the Fallen

    /// <summary>The Phase 3 crown: his hand rises and the arena gives up its dead —
    /// greatswords, axes, spears and halberds tear OUT of the floor around him in
    /// a ring of cracks, circle him once, then fire one by one in a spiral.</summary>
    private IEnumerator ArmoryOfTheFallen()
    {
        Play(CrownRaiseId, 0.2f, 1f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.3f);
        yield return Wait(0.3f, 200f);
        Play(CrownHoldId, 0.25f, 1f);
        body.Charge(0.5f);
        body.Beat(1f);
        cam?.Frame(4f, 0.18f, 0.7f, 6f, 0.5f, 1f);
        const int count = 8;
        const float slotR = 3.2f;
        var kinds = new[] { ArsenalKind.Greatsword, ArsenalKind.Axe, ArsenalKind.Spear, ArsenalKind.Halberd };
        var weapons = new SpectralBlade[count];
        var lengths = new float[count];
        var grounds = new Vector3[count];
        var spin = Random.value * 360f;
        for (var i = 0; i < count; i++)
        {
            var kind = kinds[i % kinds.Length];
            if (!WardenArsenal.Has(kind)) kind = ArsenalKind.Greatsword;
            var len = WardenArsenal.NaturalLength(kind, 1.05f);
            var a = (spin + i * 360f / count) * Mathf.Deg2Rad;
            var g = FloorPoint(transform.position) + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (slotR + 0.8f);
            grounds[i] = g;
            lengths[i] = len;
            weapons[i] = SpectralBlade.Spawn(kind, -1, len, g - Vector3.up * len, Quaternion.identity, transform, 0.05f, false);
            WardenFx.Cracks(g, 4, 1.4f, WardenFx.Crimson, 0.15f, 1.2f);
            WardenFx.Debris(g, 5, 4.5f, 1.1f);
            WardenFx.Dust(g, 3, 1f, 0.9f);
            WardenAudio.Play("stone", g, 0.6f, Random.Range(0.75f, 0.95f));
            yield return Wait(0.09f, 60f);
        }
        // Torn free: up out of the stone, then into orbit.
        var t = 0f;
        const float rise = 0.45f, orbit = 1.0f;
        while (t < rise + orbit)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 60f);
            var centre = FloorPoint(transform.position) + Vector3.up * 2.3f;
            for (var i = 0; i < count; i++)
            {
                var w = weapons[i];
                if (w == null) continue;
                var a = (spin + i * 360f / count + Mathf.Max(0f, t - rise) * 70f) * Mathf.Deg2Rad;
                var slot = centre + new Vector3(Mathf.Cos(a), Mathf.Sin(a * 2f + i) * 0.25f, Mathf.Sin(a)) * slotR;
                var outward = (slot - centre).normalized;
                if (t < rise)
                {
                    var k = t / rise;
                    var risen = grounds[i] + Vector3.up * lengths[i] * 0.2f;
                    w.transform.position = Vector3.Lerp(grounds[i] - Vector3.up * lengths[i], risen, 1f - (1f - k) * (1f - k));
                    w.transform.rotation = Quaternion.identity;
                }
                else
                {
                    var k = Mathf.Clamp01((t - rise) / 0.35f);
                    w.transform.position = Vector3.Lerp(w.transform.position, slot - outward * lengths[i] * 0.5f, 1f - Mathf.Exp(-10f * Time.deltaTime));
                    w.transform.rotation = Quaternion.Slerp(w.transform.rotation, Quaternion.FromToRotation(Vector3.up, outward), k);
                }
            }
            yield return null;
        }
        // The spiral: each one turns, swells, and goes.
        for (var i = 0; i < count; i++)
        {
            var w = weapons[i];
            if (w == null) continue;
            t = 0f;
            while (t < 0.22f && w != null)
            {
                t += Time.deltaTime;
                w.TurnToward(WardenHazard.Chest, 1000f);
                w.SetGlow(t / 0.22f);
                yield return null;
            }
            if (w == null) continue;
            var aim = WardenHazard.Chest + (playerLoco != null ? Vector3.ProjectOnPlane(playerLoco.ActualPlanarVelocity, Vector3.up) * 0.12f : Vector3.zero);
            var dir = (aim - w.Tip).normalized;
            WardenFx.Line(new[] { FloorPoint(w.Tip) + Vector3.up * 0.05f, FloorPoint(aim) + FlatDir(dir) * 4f + Vector3.up * 0.05f },
                          WardenFx.Crimson, 0.06f, 0.3f, 0.04f, 0.5f);
            w.Fire(dir, 30f, 20f, 36f, w.Kind == ArsenalKind.Axe ? 800f : 0f);
            yield return Wait(0.16f, 50f);
        }
        body.Charge(0f);
        pose?.Clear(0.3f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Wait(1.2f);
    }
}
