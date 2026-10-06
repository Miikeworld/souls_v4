using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden fight's sound cues — every dangerous attack owns one distinct
/// sound (chime = a blade forms, rising hum = the Core charging, THUMP = the
/// Core's heartbeat, WHOOM = Corestone walls flaring purple, pulse = a ring
/// leaving the floor, BOOM = the eruption). Impacts use the CC0 Kenney clips
/// copied to Resources/Audio/Sfx (Boss*); the Core's own voice is synthesised on
/// first use, so nothing here can be silently missing.
/// </summary>
public sealed class WardenAudio : MonoBehaviour
{
    private const int Rate = 32000;
    private static WardenAudio inst;
    private AudioSource[] pool;
    private int next;
    private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

    private static WardenAudio Inst
    {
        get
        {
            if (inst != null) return inst;
            inst = new GameObject("WardenAudio").AddComponent<WardenAudio>();
            inst.pool = new AudioSource[14];
            for (var i = 0; i < inst.pool.Length; i++) inst.pool[i] = inst.MakeSource("src" + i);
            return inst;
        }
    }

    private AudioSource MakeSource(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0.7f;   // arena-scale cues stay audible across the sanctum
        s.minDistance = 6f;
        s.maxDistance = 70f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.dopplerLevel = 0f;
        return s;
    }

    /// <summary>One-shot at a world position. Pitch is explicit (the crown's chimes climb).</summary>
    public static void Play(string id, Vector3 pos, float volume = 1f, float pitch = 1f)
    {
        var a = Inst;
        var clip = a.Get(id);
        if (clip == null) return;
        var s = a.pool[a.next];
        a.next = (a.next + 1) % a.pool.Length;
        s.transform.position = pos;
        s.pitch = pitch;
        s.PlayOneShot(clip, volume);
    }

    /// <summary>A held loop (drone, scrape) on its own source; stop with <see cref="StopLoop"/>.</summary>
    public static AudioSource Loop(string id, Vector3 pos, float volume = 1f, float pitch = 1f)
    {
        var a = Inst;
        var clip = a.Get(id);
        if (clip == null) return null;
        var s = a.MakeSource("loop " + id);
        s.transform.position = pos;
        s.clip = clip;
        s.loop = true;
        s.volume = volume;
        s.pitch = pitch;
        s.Play();
        return s;
    }

    public static void StopLoop(AudioSource s)
    {
        if (s == null) return;
        s.Stop();
        Destroy(s.gameObject);
    }

    private AudioClip Get(string id)
    {
        if (clips.TryGetValue(id, out var c)) return c;
        c = id switch
        {
            "chime" => Res("BossChime") ?? Synth(id, 0.9f, Chime),
            "metal" => Res("BossMetal"),
            "metalLight" => Res("BossMetalLight"),
            "stone" => Res("BossStone"),
            "shatter" => Res("BossShatter") ?? Synth(id, 0.3f, Crack),
            "thud" => Res("BossThud"),
            "slam" => Res("Slam"),
            "slash" => Res("Slash"),
            "swish" => Res("Swish"),
            "hum" => Synth(id, 1.6f, Hum),
            "thump" => Synth(id, 0.6f, Heartbeat),
            "whoom" => Synth(id, 1.0f, Whoom),
            "pulse" => Synth(id, 0.6f, Pulse),
            "boom" => Synth(id, 1.6f, Boom),
            "crack" => Synth(id, 0.3f, Crack),
            "drone" => Synth(id, 4f, Drone, loop: true),
            "scrape" => Synth(id, 1f, Scrape, loop: true),
            _ => null,
        };
        clips[id] = c;
        return c;
    }

    private static AudioClip Res(string name) => Resources.Load<AudioClip>("Audio/Sfx/" + name);

    private delegate float Wave(float t, float dur, ref State s);

    private struct State
    {
        public float phase, lp, lp2, rng;
        public uint seed;
        public float Noise()
        {
            seed = seed * 1664525u + 1013904223u;
            return (seed >> 8) / 8388608f - 1f;
        }
    }

    private static AudioClip Synth(string id, float seconds, Wave wave, bool loop = false)
    {
        var n = Mathf.CeilToInt(seconds * Rate);
        var data = new float[n];
        var s = new State { seed = 12345u + (uint)id.GetHashCode() };
        var peak = 0.0001f;
        for (var i = 0; i < n; i++)
        {
            data[i] = wave(i / (float)Rate, seconds, ref s);
            peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        }
        var gain = 0.85f / peak;
        for (var i = 0; i < n; i++) data[i] *= gain;
        if (loop)
        {
            // Short crossfade so noise-based loops don't click at the seam.
            var fade = Mathf.Min(n / 8, Rate / 20);
            for (var i = 0; i < fade; i++)
            {
                var k = i / (float)fade;
                data[i] = data[i] * k + data[n - fade + i] * (1f - k);
            }
            System.Array.Resize(ref data, n - fade);
            n -= fade;
        }
        var clip = AudioClip.Create("Warden " + id, n, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static float Lp(ref float y, float x, float cutoff) { y += (1f - Mathf.Exp(-2f * Mathf.PI * cutoff / Rate)) * (x - y); return y; }

    // Rising Core hum: a low saw-ish stack climbing an octave, swelling to the release.
    private static float Hum(float t, float dur, ref State s)
    {
        var k = t / dur;
        var f = 48f * Mathf.Pow(2f, k);
        s.phase += 2f * Mathf.PI * f / Rate;
        var v = 0.6f * Mathf.Sin(s.phase) + 0.25f * Mathf.Sin(2f * s.phase) + 0.12f * Mathf.Sin(3f * s.phase + 0.4f);
        var env = Mathf.Pow(Mathf.SmoothStep(0f, 1f, k), 1.3f) * (1f + 0.15f * Mathf.Sin(2f * Mathf.PI * 7f * t));
        if (t > dur - 0.05f) env *= (dur - t) / 0.05f;
        return v * env;
    }

    // Lub-dub: two low thumps — the Core's heartbeat.
    private static float Heartbeat(float t, float dur, ref State s)
    {
        float Beat(float tb, float amp)
        {
            if (tb < 0f) return 0f;
            var f = Mathf.Lerp(58f, 34f, Mathf.Clamp01(tb / 0.14f));
            return amp * Mathf.Sin(2f * Mathf.PI * f * tb) * Mathf.Exp(-tb / 0.075f);
        }
        var click = t < 0.012f ? Lp(ref s.lp, s.Noise(), 900f) * (1f - t / 0.012f) * 0.4f : 0f;
        return Beat(t, 1f) + Beat(t - 0.18f, 0.7f) + click;
    }

    // Purple wall flare: a swelling filtered-noise rush over a low sine.
    private static float Whoom(float t, float dur, ref State s)
    {
        var k = t / dur;
        var cutoff = k < 0.35f ? Mathf.Lerp(200f, 1300f, k / 0.35f) : Mathf.Lerp(1300f, 260f, (k - 0.35f) / 0.65f);
        var noise = Lp(ref s.lp, s.Noise(), cutoff);
        s.phase += 2f * Mathf.PI * 70f / Rate;
        var env = k < 0.3f ? k / 0.3f : Mathf.Pow(1f - (k - 0.3f) / 0.7f, 1.6f);
        return (noise * 1.4f + 0.5f * Mathf.Sin(s.phase)) * env;
    }

    // A ring leaving the floor: a falling sine thump with a dull attack.
    private static float Pulse(float t, float dur, ref State s)
    {
        var f = Mathf.Lerp(70f, 42f, Mathf.Clamp01(t / 0.25f));
        s.phase += 2f * Mathf.PI * f / Rate;
        var body = Mathf.Sin(s.phase) * Mathf.Exp(-t / 0.16f);
        var attack = t < 0.06f ? Lp(ref s.lp, s.Noise(), 600f) * (1f - t / 0.06f) : 0f;
        return body + attack * 0.8f;
    }

    // The eruption: lowpassed noise rumble with a sub boom underneath.
    private static float Boom(float t, float dur, ref State s)
    {
        var cutoff = Mathf.Lerp(450f, 110f, t / dur);
        var noise = Lp(ref s.lp, s.Noise(), cutoff) * Mathf.Exp(-t / 0.4f);
        s.phase += 2f * Mathf.PI * 40f / Rate;
        var sub = Mathf.Sin(s.phase) * Mathf.Exp(-t / 0.55f);
        return noise * 2.2f + sub * 0.8f;
    }

    private static float Crack(float t, float dur, ref State s)
    {
        var x = s.Noise();
        var low = Lp(ref s.lp, x, 1800f);
        return (x - low) * Mathf.Exp(-t / 0.05f) * (1f + (s.Noise() > 0.7f ? 1f : 0f));
    }

    private static float Chime(float t, float dur, ref State s)
    {
        var env = Mathf.Exp(-t / 0.28f);
        return (Mathf.Sin(2f * Mathf.PI * 1318.5f * t) + 0.45f * Mathf.Sin(2f * Mathf.PI * 2637f * t) + 0.2f * Mathf.Sin(2f * Mathf.PI * 3951f * t)) * env;
    }

    // Choir-ish drone: detuned pairs on a 0.25 Hz grid (exactly periodic over 4 s).
    private static float Drone(float t, float dur, ref State s)
    {
        float[] f = { 55f, 82.5f, 110f, 165f, 220f };
        float[] a = { 0.35f, 0.2f, 0.25f, 0.12f, 0.06f };
        var v = 0f;
        for (var i = 0; i < f.Length; i++)
            v += a[i] * (Mathf.Sin(2f * Mathf.PI * (f[i] - 0.25f) * t) + Mathf.Sin(2f * Mathf.PI * (f[i] + 0.25f) * t + i));
        return v * (0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 0.25f * t));
    }

    // Greatsword dragged on stone: band-limited grit with crackle.
    private static float Scrape(float t, float dur, ref State s)
    {
        var x = s.Noise();
        var a = Lp(ref s.lp, x, 2600f);
        var b = Lp(ref s.lp2, x, 500f);
        if (s.Noise() > 0.97f) s.rng = 1f;
        s.rng *= 0.995f;
        return (a - b) * (0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 11f * t)) + (a - b) * s.rng * 1.5f;
    }

    private void OnDestroy()
    {
        if (inst == this) inst = null;
    }
}
