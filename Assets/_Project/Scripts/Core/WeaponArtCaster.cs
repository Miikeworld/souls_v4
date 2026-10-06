using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The combat input router (detail §3): Q (LB) uses the selected art, 1/2/3 or
/// modifier+D-pad left/right pick the slot. Mana pays at commit; AttackController
/// runs the move through the combo's sweep/freeze/spark plumbing.
/// Techniques are the separate right-click (RB) big-sword archetype — the route
/// is resolved at press time: session/airborne context first, then modified
/// commands (modifier+RMB → full Grave Wolf, dodge+modifier+RMB → Mooncleaver),
/// then a running technique's followUp (route end → neutral), the dodge counter
/// (Iron Gale, 0.2s grace), the committed-normal branch (1st → Grave Wolf I,
/// 2nd → UpperAttack, 3rd+ → Red Reaver), else neutral Tide Splitter.
/// The dedicated ultimate input (T / D-pad up) fires Last Eclipse off the
/// earned meter — the Q+RMB chord is gone. The sprint attack stays on the
/// attack button: ≥0.6s clean sprint commits techniqueSprint (Mooncleaver Rush).
/// Auto-added by GameLoop; falls back to runtime defaults if no art assets are wired.
/// </summary>
public sealed class WeaponArtCaster : MonoBehaviour, IAbilitySlots
{
    [SerializeField] private WeaponArt[] library;
    [SerializeField] private int[] slots = { 0, 1, 2 };
    // Stable attunement identity (detail §256): `slots` are indexes into a
    // library the setup tool filters — without the parallel refs a reorder
    // silently re-points every slot at a different art.
    [SerializeField] private WeaponArt[] slotArts;

    [Header("Techniques (RMB / RB) — the big-sword archetype")]
    [Tooltip("Right-click from idle or movement.")]
    [SerializeField] private WeaponArt techniqueNeutral;
    [Tooltip("Right-click during the first normal attack.")]
    [SerializeField] private WeaponArt techniqueEarly;
    [Tooltip("Right-click during normal attack two or later.")]
    [SerializeField] private WeaponArt techniqueFinisher;
    [Tooltip("Attack button after ≥0.6s clean sprint — the dash strike (Mooncleaver Rush). Pulled by AttackController via SprintArt; not an RMB context.")]
    [SerializeField] private WeaponArt techniqueSprint;
    [Tooltip("Right-click while airborne — also the air-chase smash after a launch.")]
    [SerializeField] private WeaponArt techniqueAir;
    [Tooltip("Right-click during a dodge — the counter.")]
    [SerializeField] private WeaponArt techniqueDodge;

    [Header("Launcher (two normals → RMB, or Modifier + LMB)")]
    [Tooltip("The uppercut launcher: RMB during the second normal pops poise-vulnerable enemies airborne. AttackController also pulls it via LauncherArt.")]
    [SerializeField] private WeaponArt techniqueLauncher;
    [Tooltip("Modifier + RMB — the full Grave Wolf chain in one move (detail §86).")]
    [SerializeField] private WeaponArt techniqueMod;
    [Tooltip("Dodge recovery + modifier + RMB — Mooncleaver, the stationary rising/falling strike (detail §88).")]
    [SerializeField] private WeaponArt techniqueDodgeMod;

    [Header("Ultimate (T / D-pad up — dedicated input)")]
    [Tooltip("Last Eclipse: spends the full earned meter (100) — never mana, never a chord.")]
    [SerializeField] private WeaponArt ultimate;

    private int selected;
    private InputAction use;
    private InputAction technique;
    private InputAction ult;
    private InputAction[] selectActions;
    private InputAction slotNext, slotPrev;
    private PlayerState state;
    private PlayerMana mana;
    private AttackController attack;
    private PlayerLocomotion locomotion;
    private DodgeController dodge;

    private void Awake()
    {
        state = GetComponent<PlayerState>();
        mana = GetComponent<PlayerMana>();
        attack = GetComponent<AttackController>();
        locomotion = GetComponent<PlayerLocomotion>();
        dodge = GetComponent<DodgeController>();
        if (library == null || library.Length == 0) library = DefaultArts();
        if (slots == null || slots.Length != 3) slots = new[] { 0, 1, 2 };
        if (slotArts != null && slotArts.Length == slots.Length)
            for (var i = 0; i < slots.Length; i++)
            {
                var idx = slotArts[i] == null ? -1 : System.Array.IndexOf(library, slotArts[i]);
                if (idx >= 0) slots[i] = idx;
            }
        for (var i = 0; i < slots.Length; i++) slots[i] = Mathf.Clamp(slots[i], 0, library.Length - 1);
        if (slotArts == null || slotArts.Length != slots.Length) slotArts = new WeaponArt[slots.Length];
        for (var i = 0; i < slots.Length; i++) slotArts[i] = library[slots[i]];

        use = new InputAction("Art", InputActionType.Button);
        use.AddBinding("<Keyboard>/q");
        use.AddBinding("<Gamepad>/leftShoulder");
        technique = new InputAction("Technique", InputActionType.Button);
        technique.AddBinding("<Mouse>/rightButton");
        technique.AddBinding("<Gamepad>/rightShoulder");
        // Dedicated ultimate input (detail §60-68): T / D-pad up. The Q+RMB
        // chord is gone — no supersession, no refunds.
        ult = new InputAction("Ultimate", InputActionType.Button);
        ult.AddBinding("<Keyboard>/t");
        ult.AddBinding("<Gamepad>/dpad/up");
        // Modifier + D-pad left/right cycles the three art slots on pad —
        // unmodified left/right still cycle quick items (owned elsewhere).
        slotNext = new InputAction("ArtNext", InputActionType.Button, "<Gamepad>/dpad/right");
        slotPrev = new InputAction("ArtPrev", InputActionType.Button, "<Gamepad>/dpad/left");
        selectActions = new[]
        {
            new InputAction("Art1", InputActionType.Button, "<Keyboard>/1"),
            new InputAction("Art2", InputActionType.Button, "<Keyboard>/2"),
            new InputAction("Art3", InputActionType.Button, "<Keyboard>/3"),
        };
    }

    /// <summary>Runtime stand-ins until Setup Combat Locomotion authors the assets.</summary>
    private static WeaponArt[] DefaultArts()
    {
        WeaponArt Make(string n, string s, string st, float cost, float dmg)
        {
            var a = ScriptableObject.CreateInstance<WeaponArt>();
            a.artName = n; a.shortName = s; a.stateName = st; a.manaCost = cost; a.damagePerHit = dmg;
            return a;
        }
        return new[]
        {
            Make("Rending Draw", "REND", "Art1", 15f, 30f),
            Make("Gale Step", "GALE", "Art2", 18f, 28f),
            Make("Twin Fang", "FANG", "Art3", 22f, 22f),
        };
    }

    private void OnEnable()
    {
        use.Enable();
        technique.Enable();
        ult.Enable();
        slotNext.Enable();
        slotPrev.Enable();
        foreach (var s in selectActions) s.Enable();
        Push();
    }

    private void OnDisable()
    {
        use.Disable();
        technique.Disable();
        ult.Disable();
        slotNext.Disable();
        slotPrev.Disable();
        foreach (var s in selectActions) s.Disable();
    }

    private void OnDestroy()
    {
        use.Dispose();
        technique.Dispose();
        ult.Dispose();
        slotNext.Dispose();
        slotPrev.Dispose();
        foreach (var s in selectActions) s.Dispose();
    }

    private void Update()
    {
        if (UiGates.MenuOpen) return;
        if (state != null && state.IsDead) return;

        for (var i = 0; i < selectActions.Length; i++)
            if (selectActions[i].WasPressedThisFrame() && selected != i)
            {
                selected = i;
                Push();
            }
        // Pad slot cycle: modifier + D-pad left/right (unmodified cycles items).
        if (AttackController.ModifierHeld)
        {
            if (slotNext.WasPressedThisFrame() || slotPrev.WasPressedThisFrame())
            {
                selected = (selected + (slotNext.WasPressedThisFrame() ? 1 : slots.Length - 1)) % slots.Length;
                Push();
            }
        }

        // Dedicated ultimate input — validate + spend inside TryUltimate.
        // (Overdrive fires on its own when Crimson Instability fills.)
        if (ult.WasPressedThisFrame() && ultimate != null && attack != null)
            attack.TryUltimate(ultimate);

        // RMB routing (detail §94): restrictions → session/airborne → modified
        // commands → running follow-up → dodge context → committed branch →
        // neutral. Modifier state is sampled AT press; locked staggered targets
        // and unrelated airborne enemies never substitute the route.
        if (attack != null && technique.WasPressedThisFrame())
        {
            var mod = AttackController.ModifierHeld;
            if (attack.InAirSession)
            {
                if (mod) attack.RequestSessionTechnique(techniqueAir);   // Skyfall Edge
                else attack.TryAdvanceAerial();                          // next air strike
            }
            else if (!attack.GroundedForAction)
            {
                attack.TryAirbornePress(mod);                            // plunge / deny
            }
            else if (mod)
            {
                var dodgeRoute = dodge != null && techniqueDodgeMod != null
                    && (dodge.IsDodging || Time.unscaledTime - dodge.DodgeEndTime <= 0.2f);
                var pick = dodgeRoute ? techniqueDodgeMod : techniqueMod;
                if (pick != null && !attack.TryStartArt(pick)) mana?.Deny();
            }
            else
            {
                var running = attack.ActiveArt;
                var tech = running != null
                    ? (running.followUp != null ? running.followUp : techniqueNeutral) // route end → neutral (§96)
                    : PickTechnique();
                if (tech != null && !attack.TryStartArt(tech)) mana?.Deny();
            }
        }

        if (!use.WasPressedThisFrame() || attack == null) return;
        var art = SlotArt(selected);
        if (art == null) return;
        attack.TryStartArt(art, () => use.IsPressed());
    }

    /// <summary>The launcher art (Upper Attack) — fired on RMB during the
    /// second normal; pops poise-vulnerable enemies for the air chase.</summary>
    public WeaponArt LauncherArt => techniqueLauncher;
    /// <summary>The sprint-attack art (Mooncleaver Rush) — AttackController
    /// commits it once the short clean-sprint threshold is met.</summary>
    public WeaponArt SprintArt => techniqueSprint;

    /// <summary>Unmodified grounded RMB pick (detail §79-96): dodge recovery →
    /// Iron Gale, then the committed-normal branch — 1st → Grave Wolf I,
    /// 2nd → UpperAttack, 3rd+ → Red Reaver, idle/sprint → Tide Splitter.
    /// Nearby airborne or staggered enemies never substitute another route.</summary>
    private WeaponArt PickTechnique()
    {
        // Dodge context holds 0.2s after the dodge ends — a counter pressed on
        // the recovery edge still counts.
        if (dodge != null && (dodge.IsDodging || Time.unscaledTime - dodge.DodgeEndTime <= 0.2f)
            && techniqueDodge != null) return techniqueDodge;
        return attack.ComboBranch switch
        {
            >= 3 => techniqueFinisher,  // 3rd+ normal → Red Reaver → Bonesunder
            2 => techniqueLauncher,     // 2nd normal → the UpperAttack launcher
            1 => techniqueEarly,        // 1st normal → Grave Wolf chain
            _ => techniqueNeutral,      // idle → Tide Splitter chain
        };
    }

    private void Push()
    {
        Changed?.Invoke();
        if (Application.isPlaying) GameHud.BindAbilities(this); // edit-mode self-checks must not spawn a HUD
    }

    private WeaponArt SlotArt(int slot) =>
        library == null || library.Length == 0 ? null
            : library[Mathf.Clamp(slots[Mathf.Clamp(slot, 0, slots.Length - 1)], 0, library.Length - 1)];

    // ---------- IAbilitySlots ----------

    public event System.Action Changed;
    public int SlotCount => slots.Length;
    public int Selected => selected;
    public string SlotName(int slot) => SlotArt(slot) != null ? SlotArt(slot).shortName : "";
    public string SlotFullName(int slot) => SlotArt(slot) != null ? SlotArt(slot).artName.ToUpperInvariant() : "";
    public float SlotCost(int slot) => SlotArt(slot) != null ? SlotArt(slot).manaCost : 0f;
    public string SlotIcon(int slot) => SlotArt(slot) != null ? SlotArt(slot).icon : "";
    public int LibraryCount => library != null ? library.Length : 0;
    public string LibraryName(int index) => library[index].artName.ToUpperInvariant();
    public float LibraryCost(int index) => library[index].manaCost;

    public void Attune(int slot, int libraryIndex)
    {
        if (slot < 0 || slot >= slots.Length || libraryIndex < 0 || libraryIndex >= LibraryCount) return;
        slots[slot] = libraryIndex;
        if (slotArts != null && slotArts.Length == slots.Length)
            slotArts[slot] = library[libraryIndex];
        Push();
    }
}
