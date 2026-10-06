using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The healing flask: 10 charges, refilled at checkpoints and on respawn.
/// R / pad west plays the SoulslikeEssential Potion_Drink on a masked
/// upper-body layer — legs keep the locomotion set so the sip is a MOVING
/// drink (detail §drinks): half-speed walk/strafe, neutral input still, no
/// sprint/jump/attack/dodge/slide/wall-run. The charge spends up front with
/// no cancel or refund; the heal pours in during the swallow (0.35–0.75 of
/// the 1.6s sip) and ordinary damage can't interrupt it — death, disable,
/// teleport or a genuine loss of ground forfeits the remainder instead.
/// Pad mode-swap is modifier+west (keyboard keeps the wheel). Out of charges,
/// the press plays Potion_Empty. Auto-added by GameLoop; runtime input.
/// </summary>
public sealed class EstusFlask : MonoBehaviour
{
    // Renamed from maxCharges on purpose: the scene had saved 3, and a fresh
    // field name drops that so the new default applies without a tool run.
    [SerializeField, Min(1)] private int flaskCharges = 10;
    [SerializeField, Min(1f)] private float healAmount = 45f;
    [SerializeField, Min(0.3f)] private float sipTime = 1.6f;
    [SerializeField, Min(0.3f)] private float emptyTime = 1.0f;
    [Tooltip("Airborne seconds before a sip is genuinely off the ground — rides out CC flicker and single stair-steps.")]
    [SerializeField, Min(0.02f)] private float groundGraceTime = 0.12f;

    [Header("Bottle prop")]
    [Tooltip("Synty potion prefab shown in the hand while drinking. Null = no prop.")]
    [SerializeField] private GameObject bottlePrefab;
    [Tooltip("Which hand holds the bottle — Potion_Drink raises the right hand.")]
    [SerializeField] private HumanBodyBones bottleBone = HumanBodyBones.RightHand;
    [SerializeField] private Vector3 bottleOffset = Vector3.zero;
    [SerializeField] private Vector3 bottleEuler = new Vector3(0f, 0f, 90f);

    /// <summary>The active mode's charges — each flask owns its own pool.</summary>
    public int Charges => IsMana ? ManaCharges : HpCharges;
    public int HpCharges { get; private set; }
    public int ManaCharges { get; private set; }
    public int MaxCharges => flaskCharges;
    public bool Drinking { get; private set; }
    /// <summary>True through a real sip — the empty-shake doesn't gate actions.</summary>
    public bool Sipping => Drinking;
    /// <summary>False = health flask (estus), true = mana flask — wheel / modifier+west swaps.</summary>
    public bool IsMana { get; private set; }

    private InputAction drink;
    private PlayerState state;
    private PlayerHealth health;
    private PlayerMana mana;
    private CharacterController cc;
    private AttackController attack;
    private WeaponSocket weapon;
    private Animator animator;
    private GameObject bottle;
    private Vector3 bottleScale = Vector3.one;
    private float bottlePop;
    private float busyT, busyLen;
    private bool busy, empty;
    private int upperLayer = -2;    // -2 = unresolved; -1 = absent (base-layer fallback)
    private float upperLayerW;
    private float groundGrace;
    private Vector3 lastPos;

    private static readonly int PotionId = Animator.StringToHash("Potion");
    private static readonly int PotionEmptyId = Animator.StringToHash("PotionEmpty");
    private static readonly int PotionBaseId = Animator.StringToHash("Base Layer.Potion");
    private static readonly int PotionEmptyBaseId = Animator.StringToHash("Base Layer.PotionEmpty");
    private static readonly int LocomotionId = Animator.StringToHash("Base Layer.Locomotion");
    private static readonly int CombatMoveId = Animator.StringToHash("Base Layer.CombatMove");

    private void Awake()
    {
        state = GetComponent<PlayerState>();
        health = GetComponent<PlayerHealth>();
        mana = GetComponent<PlayerMana>();
        cc = GetComponent<CharacterController>();
        attack = GetComponent<AttackController>();
        weapon = GetComponent<WeaponSocket>();
        foreach (var a in GetComponentsInChildren<Animator>(true))
            if (a != null && a.enabled && a.runtimeAnimatorController != null && a.avatar != null && a.avatar.isHuman)
            { animator = a; break; }
        HpCharges = ManaCharges = flaskCharges;
        drink = new InputAction("Flask", InputActionType.Button);
        drink.AddBinding("<Keyboard>/r");
        drink.AddBinding("<Gamepad>/buttonWest");
        BuildBottle();
    }

    /// <summary>Parents the potion prop to the chosen hand bone. The rig's
    /// bones may run at 0.01 scale, so the prop compensates to render at world
    /// size. The vendor material predates URP — patch it or the bottle is magenta.</summary>
    private void BuildBottle()
    {
        if (bottlePrefab == null || animator == null || !animator.isHuman) return;
        var hand = animator.GetBoneTransform(bottleBone);
        if (hand == null) return;
        bottle = Instantiate(bottlePrefab, hand, false);
        bottle.name = "FlaskBottle";
        var ls = hand.lossyScale.x;
        bottleScale = Vector3.one * (ls > 0.0001f ? 1f / ls : 1f);
        bottle.transform.localScale = bottleScale;
        bottle.transform.localPosition = bottleOffset;
        bottle.transform.localRotation = Quaternion.Euler(bottleEuler);
        foreach (var c in bottle.GetComponentsInChildren<Collider>()) Destroy(c);
        VendorUrp.FixTree(bottle);
        bottle.SetActive(false);
    }

    private void OnEnable()
    {
        drink.Enable();
        lastPos = transform.position;
        GameHud.SetFlask(Charges, flaskCharges);
        GameHud.SetFlaskMode(IsMana);
    }
    private void OnDisable() { drink.Disable(); Stop(); }
    private void OnDestroy() => drink.Dispose();

    /// <summary>Checkpoint/respawn refill — tops up BOTH pools.</summary>
    public void Refill()
    {
        HpCharges = ManaCharges = flaskCharges;
        GameHud.SetFlask(Charges, flaskCharges);
    }

    /// <summary>The masked upper-body layer index — resolved lazily so a
    /// controller rebuilt after this Awake still gets found.</summary>
    private int UpperLayer
    {
        get
        {
            if (upperLayer == -2)
                upperLayer = animator != null ? animator.GetLayerIndex("UpperBody") : -1;
            return upperLayer;
        }
    }

    private void Update()
    {
        // Bottle visibility is a delayed pop — it materializes once the hand
        // is actually raised (~10% into the sip) and shrinks away at the end,
        // so it never hard-cuts in or out.
        if (bottle != null)
        {
            var want = busy && busyT > busyLen * 0.1f && busyT < busyLen - 0.06f;
            bottlePop = Mathf.MoveTowards(bottlePop, want ? 1f : 0f, Time.unscaledDeltaTime / 0.1f);
            var show = bottlePop > 0.01f;
            if (bottle.activeSelf != show) bottle.SetActive(show);
            if (show) bottle.transform.localScale = bottleScale * PersonaUi.EaseOutBack(Mathf.Clamp01(bottlePop), 1.4f);
        }

        // The masked layer fades in/out so the arms never hard-swap.
        var targetW = busy ? 1f : 0f;
        if (UpperLayer >= 0 && animator != null && !Mathf.Approximately(upperLayerW, targetW))
        {
            upperLayerW = Mathf.MoveTowards(upperLayerW, targetW, Time.deltaTime / 0.12f);
            animator.SetLayerWeight(UpperLayer, upperLayerW);
        }

        if (state != null && state.IsDead) { Stop(); return; }

        // The sip keeps running while menus gate input — menus don't pause
        // gameplay (detail §drinks). This block must stay above the menu gate.
        if (busy)
        {
            busyT += Time.deltaTime;
            // Forfeit — never refund — on a genuine loss of ground (walked off
            // a ledge) or a teleport (position jump without freefall).
            var pos = transform.position;
            var teleported = (pos - lastPos).sqrMagnitude > 6.25f;
            var offGround = cc != null && !cc.isGrounded;
            groundGrace = offGround ? groundGrace + Time.deltaTime : 0f;
            lastPos = pos;
            if (teleported || groundGrace >= groundGraceTime) { Stop(); return; }
            if (!empty)
            {
                // Heal only across the swallow — the raise and lower give nothing.
                var from = sipTime * 0.35f;
                var to = sipTime * 0.75f;
                var prev = busyT - Time.deltaTime;
                var overlap = Mathf.Min(busyT, to) - Mathf.Max(prev, from);
                if (overlap > 0f)
                {
                    if (IsMana) mana?.Restore(healAmount * overlap / (to - from));
                    else health.Heal(healAmount * overlap / (to - from));
                }
            }
            if (busyT >= busyLen) Stop();
            return;
        }
        lastPos = transform.position;

        if (UiGates.MenuOpen) return;

        var padPressed = drink.WasPressedThisFrame() && drink.activeControl != null
                         && drink.activeControl.device is Gamepad;
        if (padPressed && AttackController.ModifierHeld)
        {
            SetMode(!IsMana); // pad flask-mode = modifier + west face button
            return;
        }

        // Scroll wheel swaps the flask on keyboard: up = mana, down = estus.
        var scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        if (scroll > 0.05f && !IsMana) SetMode(true);
        else if (scroll < -0.05f && IsMana) SetMode(false);

        var blocked = state == null || state.IsRooted || state.IsDisplacing || state.IsDrinking
                      || GameLoop.IsResting
                      || (attack != null && attack.IsAttacking)
                      || (cc != null && !cc.isGrounded);
        if (blocked || drink == null || !drink.WasPressedThisFrame()) return;

        empty = Charges <= 0;
        if (empty) GameHud.Toast("NO FLASKS");
        else
        {
            // The charge spends at commit — a forfeit later never refunds it.
            if (IsMana) ManaCharges--; else HpCharges--;
            GameHud.SetFlask(Charges, flaskCharges);
        }
        busy = true;
        Drinking = !empty;
        busyT = 0f;
        busyLen = empty ? emptyTime : sipTime;
        groundGrace = 0f;
        // Explicit restriction flag — never a global IsRooted write that could
        // clear another action's ownership (detail §drinks).
        if (state != null) state.IsDrinking = true;
        // The bottle owns the hand; the equipped blade is stowed for the sip
        // and the normal combat rule restores whatever's current afterward.
        if (!empty && weapon != null) weapon.Stowed = true;
        var layer = UpperLayer;
        var id = empty ? PotionEmptyId : PotionId;
        if (animator != null)
        {
            if (layer >= 0 && animator.HasState(layer, id))
                animator.CrossFadeInFixedTime(id, 0.15f, layer);
            else if (layer < 0)
            {
                var baseId = empty ? PotionEmptyBaseId : PotionBaseId;
                if (animator.HasState(0, baseId)) animator.CrossFadeInFixedTime(baseId, 0.15f, 0);
            }
        }
    }

    private void SetMode(bool manaMode)
    {
        IsMana = manaMode;
        GameHud.SetFlask(Charges, flaskCharges); // the swapped pool's own count
        GameHud.SetFlaskMode(manaMode);
    }

    private void Stop()
    {
        if (!busy) return;
        busy = false;
        Drinking = false;
        groundGrace = 0f;
        if (state != null) state.IsDrinking = false;
        if (weapon != null) weapon.Stowed = false;
        var layer = UpperLayer;
        if (layer < 0 && animator != null)
            animator.CrossFadeInFixedTime(weapon != null && weapon.InCombat ? CombatMoveId : LocomotionId, 0.15f, 0);
        // The masked layer just fades out — upperLayerW decays in Update.
    }
}
