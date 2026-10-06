using System.Collections;
using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// Short boss camera shots in the Genshin / Souls-remake mould, played on a
/// temporary high-priority CinemachineCamera so the brain blends in and back
/// out (no hard cuts into gameplay). Each shot is a list of keys relative to
/// the boss — position (boss-local), look point (boss-local) and FOV — eased
/// between in unscaled time. While a shot owns the screen the player is rooted
/// and invulnerable; control returns as the blend back starts.
///   Intro  — low wide reveal → slow push up the body → face close-up on the roar.
///   Phase  — over-the-shoulder from the player, slight orbit while it roars.
///   Death  — slowed world, a wide orbit as it falls.
/// </summary>
public sealed class BossCinematics : MonoBehaviour
{
    public struct Key
    {
        public float t;           // seconds from shot start
        public Vector3 pos, look; // boss-local (scaled by bossScale)
        public float fov;
        public Key(float t, Vector3 pos, Vector3 look, float fov) { this.t = t; this.pos = pos; this.look = look; this.fov = fov; }
    }

    public static bool Playing { get; private set; }

    private static BossCinematics runner;
    private CinemachineCamera cam;

    private static BossCinematics Runner()
    {
        if (runner != null) return runner;
        var go = new GameObject("BossCinematics");
        runner = go.AddComponent<BossCinematics>();
        runner.cam = go.AddComponent<CinemachineCamera>();
        runner.cam.Priority = 0;
        runner.cam.enabled = false;
        return runner;
    }

    /// <summary>Play keys around <paramref name="boss"/>; <paramref name="beats"/> fire at
    /// their time (roar, shake). <paramref name="slowMo"/> &lt; 1 slows the world.</summary>
    public static void Play(Transform boss, float bossScale, Key[] keys, (float t, System.Action act)[] beats = null,
                            float blendIn = 0.6f, float blendOut = 0.9f, float slowMo = 1f)
    {
        if (boss == null || keys == null || keys.Length == 0) return;
        // Never cut a running shot: it owns the player's rooted/invulnerable flags
        // and the brain blend, and restores them only at its own end.
        if (Playing) return;
        var r = Runner();
        r.StartCoroutine(r.Run(boss, bossScale, keys, beats, blendIn, blendOut, slowMo));
    }

    private IEnumerator Run(Transform boss, float s, Key[] keys, (float t, System.Action act)[] beats,
                            float blendIn, float blendOut, float slowMo)
    {
        Playing = true;
        var brain = FindFirstObjectByType<CinemachineBrain>();
        var oldBlend = brain != null ? brain.DefaultBlend : default;
        if (brain != null) brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, blendIn);
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        var state = loco != null ? loco.GetComponent<PlayerState>() : null;
        var wasRooted = state != null && state.IsRooted;
        var wasInvuln = state != null && state.IsInvulnerable;
        if (state != null) { state.IsRooted = true; state.IsInvulnerable = true; }
        var oldScale = Time.timeScale;
        if (slowMo < 1f) Time.timeScale = slowMo;

        // Snapshot the boss frame at shot start — a shot shouldn't wobble with every step.
        var origin = boss.position;
        var rot = Quaternion.Euler(0f, boss.eulerAngles.y, 0f);
        Vector3 W(Vector3 local) => origin + rot * (local * s);

        cam.enabled = true;
        cam.Priority = 900;
        var fired = new bool[beats?.Length ?? 0];
        var end = keys[keys.Length - 1].t;
        var t = 0f;
        Place(keys, 0f, W);
        while (t < end)
        {
            t += Time.unscaledDeltaTime;
            Place(keys, t, W);
            if (beats != null)
                for (var i = 0; i < beats.Length; i++)
                    if (!fired[i] && t >= beats[i].t) { fired[i] = true; beats[i].act?.Invoke(); }
            yield return null;
        }
        if (slowMo < 1f) Time.timeScale = oldScale;
        if (brain != null) brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, blendOut);
        cam.Priority = 0;
        if (state != null) { state.IsRooted = wasRooted; state.IsInvulnerable = wasInvuln; }
        Playing = false;
        // Keep the shot camera alive until the blend back has finished.
        var wait = 0f;
        while (wait < blendOut + 0.1f) { wait += Time.unscaledDeltaTime; yield return null; }
        cam.enabled = false;
        if (brain != null) brain.DefaultBlend = oldBlend;
    }

    private void Place(Key[] keys, float t, System.Func<Vector3, Vector3> world)
    {
        var i = 0;
        while (i < keys.Length - 1 && keys[i + 1].t < t) i++;
        var a = keys[i];
        var b = keys[Mathf.Min(i + 1, keys.Length - 1)];
        var k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 1f;
        k = k * k * (3f - 2f * k); // ease in/out between keys
        var pos = Vector3.Lerp(world(a.pos), world(b.pos), k);
        var look = Vector3.Lerp(world(a.look), world(b.look), k);
        transform.position = pos;
        var dir = look - pos;
        if (dir.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        var lens = cam.Lens;
        lens.FieldOfView = Mathf.Lerp(a.fov, b.fov, k);
        cam.Lens = lens;
    }

    private void OnDestroy()
    {
        if (runner == this) { runner = null; Playing = false; }
    }
}
