using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The item satchel: stacks of consumables/key items, the set of weapon sets
/// the player owns, and one cycling quick-slot on the HUD. Auto-added by
/// GameLoop — zero scene wiring — and persisted through SaveGame.
/// Field input: F / pad dpad-down uses the quick item; C,V / dpad left,right
/// cycle it. Drinkables borrow the Potion sip (rooted, heal on the swallow —
/// the EstusFlask idiom); souls pay out instantly; throw items launch through
/// the existing ArtProjectile plumbing.
/// </summary>
public sealed class Inventory : MonoBehaviour
{
    [System.Serializable]
    public sealed class Entry
    {
        public ItemDef item;
        public int count;
    }

    [Tooltip("Fallback stock if SaveGame has nothing — empty is fine.")]
    [SerializeField] private ItemDef[] starterItems;

    private readonly List<Entry> entries = new();
    private readonly List<WeaponSet> ownedSets = new();
    private int quickIndex;

    private InputAction useAction, cycleFwd, cycleBack, openMenu;
    private PlayerState state;
    private PlayerHealth health;
    private PlayerMana mana;
    private PlayerStamina stamina;
    private CharacterController cc;
    private AttackController attack;
    private LockOnController lockOn;
    private Animator animator;
    private float busyT, busyLen;
    private bool busy;
    private ItemDef busyItem;

    private static readonly int PotionId = Animator.StringToHash("Base Layer.Potion");
    private static readonly int LocomotionId = Animator.StringToHash("Base Layer.Locomotion");
    private static readonly int CombatMoveId = Animator.StringToHash("Base Layer.CombatMove");

    public event System.Action Changed;
    public int EntryCount => entries.Count;
    public Entry Get(int i) => entries[i];

    /// <summary>The cycling quick-slot: the nth consumable row, wrapped.</summary>
    public ItemDef QuickItem
    {
        get
        {
            var usable = Usable();
            return usable.Count == 0 ? null : usable[Mathf.Abs(quickIndex) % usable.Count].item;
        }
    }

    private void Awake()
    {
        state = GetComponent<PlayerState>();
        health = GetComponent<PlayerHealth>();
        mana = GetComponent<PlayerMana>();
        stamina = GetComponent<PlayerStamina>();
        cc = GetComponent<CharacterController>();
        attack = GetComponent<AttackController>();
        lockOn = GetComponent<LockOnController>();
        foreach (var a in GetComponentsInChildren<Animator>(true))
            if (a != null && a.enabled && a.runtimeAnimatorController != null && a.avatar != null && a.avatar.isHuman)
            { animator = a; break; }

        useAction = new InputAction("ItemUse", InputActionType.Button);
        useAction.AddBinding("<Keyboard>/f");
        useAction.AddBinding("<Gamepad>/dpad/down");
        cycleFwd = new InputAction("ItemNext", InputActionType.Button);
        cycleFwd.AddBinding("<Keyboard>/v");
        cycleFwd.AddBinding("<Gamepad>/dpad/right");
        cycleBack = new InputAction("ItemPrev", InputActionType.Button);
        cycleBack.AddBinding("<Keyboard>/c");
        cycleBack.AddBinding("<Gamepad>/dpad/left");
        openMenu = new InputAction("Items", InputActionType.Button);
        openMenu.AddBinding("<Keyboard>/tab");
        openMenu.AddBinding("<Keyboard>/i");
        openMenu.AddBinding("<Gamepad>/select");

        if (starterItems != null)
            foreach (var s in starterItems) if (s != null) Add(s, 2);
    }

    private void OnEnable()
    {
        useAction.Enable();
        cycleFwd.Enable();
        cycleBack.Enable();
        openMenu.Enable();
        Push();
    }

    private void OnDisable()
    {
        useAction.Disable();
        cycleFwd.Disable();
        cycleBack.Disable();
        openMenu.Disable();
        Stop();
    }

    private void OnDestroy()
    {
        useAction.Dispose();
        cycleFwd.Dispose();
        cycleBack.Dispose();
        openMenu.Dispose();
    }

    // ---------- stack ops ----------

    public int Count(ItemDef item)
    {
        var e = entries.Find(x => x.item == item);
        return e != null ? e.count : 0;
    }

    public void Add(ItemDef item, int n = 1)
    {
        if (item == null || n <= 0) return;
        var e = entries.Find(x => x.item == item);
        if (e != null) e.count = Mathf.Min(item.stackMax, e.count + n);
        else entries.Add(new Entry { item = item, count = Mathf.Min(item.stackMax, n) });
        Push();
    }

    /// <summary>Consume one unit if any — the cost of using an item.</summary>
    public bool TryConsume(ItemDef item)
    {
        var e = entries.Find(x => x.item == item);
        if (e == null || e.count <= 0) return false;
        if (--e.count <= 0) entries.Remove(e);
        Push();
        return true;
    }

    // ---------- weapons ----------

    public bool Owns(WeaponSet set) => set != null && ownedSets.Contains(set);

    public void Own(WeaponSet set)
    {
        if (set == null || ownedSets.Contains(set)) return;
        ownedSets.Add(set);
        Push();
    }

    public int OwnedCount => ownedSets.Count;
    public WeaponSet OwnedAt(int i) => ownedSets[i];

    // ---------- use ----------

    private void Update()
    {
        if (busy)
        {
            busyT += Time.deltaTime;
            // Pour window — same swallow arc as the flask.
            var from = busyLen * 0.35f;
            var to = busyLen * 0.75f;
            var overlap = Mathf.Min(busyT, to) - Mathf.Max(busyT - Time.deltaTime, from);
            if (overlap > 0f && busyItem != null)
                Pour(busyItem.magnitude * overlap / (to - from));
            if (busyT >= busyLen) Stop();
            return;
        }

        if (UiGates.MenuOpen) return;
        if (state == null || state.IsDead) return;

        // Modifier (LAlt/LT) makes D-pad left/right the art-slot cycle —
        // quick-item cycling is the unmodified meaning only (detail §71).
        if (!AttackController.ModifierHeld)
        {
            if (cycleFwd.WasPressedThisFrame()) Cycle(1);
            if (cycleBack.WasPressedThisFrame()) Cycle(-1);
        }
        if (useAction.WasPressedThisFrame()) UseQuick();
        if (openMenu.WasPressedThisFrame()) InventoryMenu.Toggle();
    }

    private void Cycle(int dir)
    {
        var n = Usable().Count;
        if (n == 0) return;
        quickIndex = ((quickIndex + dir) % n + n) % n;
        Push();
    }

    public void UseQuick() => Use(QuickItem);

    /// <summary>Menu use: instant effect at full magnitude, no sip — the
    /// inventory screen's USE action. Throws still launch forward.</summary>
    public bool UseInstant(ItemDef item)
    {
        if (item == null || item.kind != ItemDef.Kind.Consumable || busy) return false;
        if (!TryConsume(item)) return false;
        switch (item.effect)
        {
            case ItemDef.Effect.Souls:
                SoulsWallet.Add(Mathf.RoundToInt(item.magnitude));
                GameHud.Toast("+" + Mathf.RoundToInt(item.magnitude) + " SOULS");
                break;
            case ItemDef.Effect.Throw:
                Throw(item);
                break;
            default:
                busyItem = item; // Pour() reads the effect off busyItem
                Pour(item.magnitude);
                busyItem = null;
                break;
        }
        return true;
    }

    /// <summary>Use one unit of <paramref name="item"/>. Drinkables play the
    /// sip and pay out across the swallow; souls/throws resolve instantly.</summary>
    public void Use(ItemDef item)
    {
        if (item == null || busy || item.kind != ItemDef.Kind.Consumable) return;
        if (state.IsRooted || state.IsDisplacing || (attack != null && attack.IsAttacking)) return;

        switch (item.effect)
        {
            case ItemDef.Effect.Souls:
                if (!TryConsume(item)) return;
                SoulsWallet.Add(Mathf.RoundToInt(item.magnitude));
                GameHud.Toast("+" + Mathf.RoundToInt(item.magnitude) + " SOULS");
                return;
            case ItemDef.Effect.Throw:
                if (!TryConsume(item)) return;
                Throw(item);
                return;
            default:
                if (cc != null && !cc.isGrounded) return;
                if (!TryConsume(item)) return;
                busy = true;
                busyItem = item;
                busyT = 0f;
                busyLen = 1.6f;
                state.IsRooted = true;
                if (animator != null && animator.HasState(0, PotionId))
                    animator.CrossFadeInFixedTime(PotionId, 0.15f, 0);
                return;
        }
    }

    private void Pour(float amount)
    {
        switch (busyItem.effect)
        {
            case ItemDef.Effect.Heal: health?.Heal(amount); break;
            case ItemDef.Effect.Mana: mana?.Restore(amount); break;
            case ItemDef.Effect.Stamina:
                stamina?.Refill();
                busyItem = null; // one Refill tops the bar — pouring per-frame would spam it
                break;
        }
    }

    private void Throw(ItemDef item)
    {
        var cam = Camera.main;
        var fwd = cam != null ? cam.transform.forward : transform.forward;
        fwd.y = Mathf.Max(fwd.y, 0.05f); // slight loft so it arcs over ledges
        var spec = item.projectile ?? new ProjectileSpec { speed = 14f, damage = item.magnitude, radius = 0.4f, life = 3f };
        spec.damage = item.magnitude;
        ArtProjectile.Launch(spec, transform.position + Vector3.up * 1.3f, fwd.normalized,
            lockOn != null ? lockOn.CurrentTarget?.transform : null, transform, 15f);
    }

    private void Stop()
    {
        if (!busy) return;
        busy = false;
        busyItem = null;
        if (state != null && !state.IsDead) state.IsRooted = false;
        if (animator != null && animator.HasState(0, LocomotionId))
            animator.CrossFadeInFixedTime(LocomotionId, 0.15f, 0);
    }

    // ---------- save helpers ----------

    public void ClearAll()
    {
        entries.Clear();
        ownedSets.Clear();
        quickIndex = 0;
        Push();
    }

    /// <summary>id:count pairs for the save blob.</summary>
    public List<string> Serialize()
    {
        var list = new List<string>();
        foreach (var e in entries)
            if (e.item != null) list.Add(e.item.itemId + ":" + e.count);
        return list;
    }

    /// <summary>Resolve a saved id against the loaded ItemDef assets.</summary>
    public void Restore(List<string> ids, IList<ItemDef> catalog)
    {
        entries.Clear();
        foreach (var pair in ids)
        {
            var sep = pair.LastIndexOf(':');
            if (sep <= 0) continue;
            var id = pair.Substring(0, sep);
            var item = FindById(catalog, id);
            if (item == null || !int.TryParse(pair.Substring(sep + 1), out var n)) continue;
            entries.Add(new Entry { item = item, count = Mathf.Min(item.stackMax, n) });
        }
        Push();
    }

    public void RestoreWeapons(IList<string> names)
    {
        ownedSets.Clear();
        // WeaponSets must be loaded to be found — the two known sets are
        // referenced by PlayerCustomizer, so they're resident. ponytail:
        // grows a catalog if a set is ever unreferenced.
        foreach (var set in Resources.FindObjectsOfTypeAll<WeaponSet>())
            if (set != null && names.Contains(set.name)) ownedSets.Add(set);
        Push();
    }

    private static ItemDef FindById(IList<ItemDef> catalog, string id)
    {
        for (var i = 0; i < catalog.Count; i++)
            if (catalog[i] != null && catalog[i].itemId == id) return catalog[i];
        return null;
    }

    private List<Entry> Usable()
    {
        var list = new List<Entry>();
        foreach (var e in entries)
            if (e.item != null && e.item.kind == ItemDef.Kind.Consumable && e.count > 0)
                list.Add(e);
        return list;
    }

    private void Push()
    {
        Changed?.Invoke();
        if (Application.isPlaying) GameHud.BindInventory(this);
    }
}
