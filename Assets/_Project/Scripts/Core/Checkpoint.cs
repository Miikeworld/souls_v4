using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A checkpoint. Unlit, it offers "LIGHT CHECKPOINT" — the ignite animation
/// kindles it, it becomes the respawn point and joins the travel network.
/// Lit, it offers "REST": the player sits, everything refills, every enemy
/// resets, and the checkpoint menu (REST / ARTS / TRAVEL / LEAVE) opens.
/// Builds its own primitive visual (sword stub in an ash mound, ember + light)
/// so it works with zero scene setup.
/// </summary>
public sealed class Checkpoint : MonoBehaviour
{
    [SerializeField] private string displayName = "Checkpoint";
    [Tooltip("Lit from the start — the spawn checkpoint, so death always has somewhere to go.")]
    [SerializeField] private bool litAtStart;
    [SerializeField, Min(0.5f)] private float radius = 3f;

    public static readonly List<Checkpoint> All = new();
    public bool Lit { get; private set; }
    public string DisplayName => displayName;

    /// <summary>Where the player sits/respawns — in front of the fire, facing it.</summary>
    public Vector3 RestPosition => transform.position + transform.forward * 1.3f;
    public Quaternion RestRotation => Quaternion.LookRotation(-transform.forward, Vector3.up);

    private static Checkpoint promptOwner;
    private static readonly int IgniteId = Animator.StringToHash("Base Layer.CheckpointIgnite");

    private InputAction interact;
    private Transform player;
    private PlayerState playerState;
    private Animator playerAnimator;
    private Renderer emberRend;
    private Light glow;
    private float pulseT, flare;
    private bool igniting;

    /// <summary>The world's first checkpoint — creates one (lit) if the scene has none.</summary>
    public static Checkpoint EnsureNear(Vector3 pos)
    {
        var existing = FindFirstObjectByType<Checkpoint>();
        if (existing != null) return existing;
        var go = new GameObject("Checkpoint A");
        go.transform.position = pos;
        var c = go.AddComponent<Checkpoint>();
        c.litAtStart = true;
        c.Lit = true;
        return c;
    }

    /// <summary>Lit checkpoints other than <paramref name="current"/> — the travel list.</summary>
    public static List<Checkpoint> TravelTargets(Checkpoint current) => TravelTargets(current, All);

    public static List<Checkpoint> TravelTargets(Checkpoint current, IEnumerable<Checkpoint> pool)
    {
        var list = new List<Checkpoint>();
        foreach (var c in pool)
            if (c != null && c != current && c.Lit) list.Add(c);
        return list;
    }

    /// <summary>Lights silently (spawn checkpoint fallback).</summary>
    public void LightSilently() => Lit = true;

    // ---------- visual ----------

    /// <summary>Ash mound + upright sword stub + ember + light — all primitives.</summary>
    private void BuildVisual()
    {
        var mound = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        mound.name = "AshMound";
        mound.transform.SetParent(transform, false);
        mound.transform.localScale = new Vector3(1.6f, 0.45f, 1.6f);
        mound.transform.localPosition = Vector3.up * 0.12f;
        Flatten(mound, new Color(0.16f, 0.14f, 0.15f));

        var sword = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sword.name = "SwordStub";
        sword.transform.SetParent(transform, false);
        sword.transform.localScale = new Vector3(0.07f, 0.85f, 0.16f);
        sword.transform.localPosition = Vector3.up * 0.55f;
        sword.transform.localRotation = Quaternion.Euler(0f, 0f, -12f);
        Flatten(sword, new Color(0.42f, 0.4f, 0.44f));
        var guard = GameObject.CreatePrimitive(PrimitiveType.Cube);
        guard.name = "Guard";
        guard.transform.SetParent(sword.transform, false);
        guard.transform.localScale = new Vector3(4.5f, 0.22f, 1.1f);
        guard.transform.localPosition = new Vector3(0f, 0.28f, 0f);
        Flatten(guard, new Color(0.3f, 0.22f, 0.14f));

        var ember = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ember.name = "Ember";
        ember.transform.SetParent(transform, false);
        ember.transform.localScale = Vector3.one * 0.5f;
        ember.transform.localPosition = Vector3.up * 0.22f;
        emberRend = Flatten(ember, new Color(0.4f, 0.08f, 0.02f));
        emberRend.material.EnableKeyword("_EMISSION");

        var lightGo = new GameObject("Glow");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = Vector3.up * 0.8f;
        glow = lightGo.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = new Color(1f, 0.55f, 0.2f);
        glow.range = 4f;
        glow.intensity = 0f;
    }

    private static Renderer Flatten(GameObject primitive, Color c)
    {
        var col = primitive.GetComponent<Collider>();
        if (col != null) Destroy(col);
        var rend = primitive.GetComponent<Renderer>();
        rend.material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = c };
        return rend;
    }

    // ---------- lifecycle ----------

    private void Awake()
    {
        if (transform.childCount == 0) BuildVisual(); // editor-placed empties dress themselves
        else
        {
            var e = transform.Find("Ember");
            if (e != null) emberRend = e.GetComponent<Renderer>();
            var g = transform.Find("Glow");
            if (g != null) glow = g.GetComponent<Light>();
        }
        Lit = Lit || litAtStart;
        interact = new InputAction("Interact", InputActionType.Button);
        interact.AddBinding("<Keyboard>/e");
        interact.AddBinding("<Gamepad>/buttonNorth");
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null)
        {
            player = loco.transform;
            playerState = loco.GetComponent<PlayerState>();
            foreach (var a in loco.GetComponentsInChildren<Animator>(true))
                if (a != null && a.enabled && a.runtimeAnimatorController != null && a.avatar != null && a.avatar.isHuman)
                { playerAnimator = a; break; }
        }
    }

    private void OnEnable()
    {
        All.Add(this);
        interact.Enable();
    }

    private void OnDisable()
    {
        All.Remove(this);
        interact.Disable();
        if (promptOwner == this) { GameHud.HidePrompt(); promptOwner = null; }
    }

    private void OnDestroy() => interact.Dispose();

    private void Update()
    {
        TickVisual(Time.deltaTime);
        if (player == null || UiGates.MenuOpen) return;

        var busy = igniting || GameLoop.IsResting
                   || (playerState != null && (playerState.IsDead || playerState.IsDisplacing || playerState.IsRooted));
        var near = !busy && Vector3.Distance(player.position, transform.position) <= radius;

        if (!near)
        {
            if (promptOwner == this) { GameHud.HidePrompt(); promptOwner = null; }
            return;
        }
        promptOwner = this;
        GameHud.ShowPrompt("E", Lit ? "REST" : "LIGHT CHECKPOINT");
        if (!interact.WasPressedThisFrame()) return;

        GameHud.HidePrompt();
        promptOwner = null;
        if (Lit) GameLoop.Ensure().RestAt(this);
        else StartCoroutine(Ignite());
    }

    private void TickVisual(float dt)
    {
        pulseT += dt;
        flare = Mathf.Max(0f, flare - dt * 1.2f);
        var pulse = 0.6f + Mathf.PingPong(pulseT, 1f) * 0.9f;
        if (emberRend != null)
            emberRend.material.SetColor("_EmissionColor",
                Lit ? new Color(1f, 0.25f, 0.05f) * (pulse + flare * 5f) : new Color(0.08f, 0.07f, 0.07f));
        if (glow != null)
            glow.intensity = Lit ? 1.2f + Mathf.PingPong(pulseT * 0.8f, 0.8f) + flare * 5f : 0f;
    }

    // ---------- lighting ----------

    private IEnumerator Ignite()
    {
        igniting = true;
        if (playerState != null) playerState.IsRooted = true;
        FacePlayer();

        var len = 1.4f;
        if (playerAnimator != null && playerAnimator.HasState(0, IgniteId))
        {
            playerAnimator.CrossFadeInFixedTime(IgniteId, 0.15f, 0);
            yield return null; // let the crossfade register before reading lengths
            var next = playerAnimator.GetNextAnimatorStateInfo(0);
            if (next.fullPathHash == IgniteId && next.length > 0.1f) len = next.length;
        }

        yield return new WaitForSeconds(len * 0.6f);
        Lit = true;
        flare = 1f;
        GameLoop.Ensure().SetCheckpoint(RestPosition, RestRotation);
        GameHud.Banner("CHECKPOINT LIT");

        yield return new WaitForSeconds(len * 0.4f);
        GameLoop.PlayLocomotion(playerAnimator);
        if (playerState != null && !playerState.IsDead) playerState.IsRooted = false;
        igniting = false;
    }

    private void FacePlayer()
    {
        if (player == null) return;
        var to = transform.position - player.position;
        to.y = 0f;
        if (to.sqrMagnitude > 0.001f) player.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
    }
}
