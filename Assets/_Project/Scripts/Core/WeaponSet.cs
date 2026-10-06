using UnityEngine;

/// <summary>
/// A weapon's combat identity: the mesh socketed to the hand, the locked-move
/// speed, and an AnimatorOverrideController that swaps this weapon's clips onto
/// the shared combat states (the base controller is authored with Big Sword
/// clips; katana and future weapons override rather than duplicate states).
/// Dodge/attack clip lists are consumed by DodgeController/AttackController.
/// </summary>
[CreateAssetMenu(menuName = "Souls/Weapon Set", fileName = "WeaponSet")]
public sealed class WeaponSet : ScriptableObject
{
    public string displayName = "Weapon";

    [Header("Visual")]
    [Tooltip("Weapon model instantiated under the hand bone.")]
    public GameObject weaponPrefab;
    [Tooltip("Optional name fragment to match a hand-placed weapon child (e.g. 'katana', 'claymore'). Lets multiple placed weapons coexist — the matching one is adopted, others stay hidden.")]
    public string socketNameHint;
    [Tooltip("Local offset/rotation inside the hand bone. Tune per weapon + rig.")]
    public Vector3 handLocalPosition;
    public Vector3 handLocalEuler;
    [Tooltip("The weapon-local axis running handle→tip. Used for blade roll and grip slides.")]
    public Vector3 bladeAxis = Vector3.forward;
    [Tooltip("Measured blade-only endpoints in the weapon root's local frame. Manual setup calibrates these without changing the vendor mesh.")]
    public bool bladeGeometryCalibrated;
    public Vector3 bladeBaseLocal;
    public Vector3 bladeTipLocal;
    [Tooltip("Extra rotation around the blade axis — spins the sword in the grip without changing which way the blade points.")]
    public float bladeRoll;
    [Tooltip("Constant socket-frame correction applied on top of the animated weapon bone — rotate the weapon in the Scene view during Play to live-tune (writes back here).")]
    public Vector3 weaponRotOffset;
    [Tooltip("Hand-space position correction on top of the anchored grip — move the weapon in the Scene view during Play to live-tune (writes back here).")]
    public Vector3 weaponPosOffset;
    [Tooltip("How much of the source rig's authored hand→socket offset to follow — 0 locks the grip to the palm, 1 tracks the authored socket placement. Lower it if the weapon floats off the hand.")]
    [Range(0f, 1f)] public float socketDeltaScale = 1f;
    public Vector3 handLocalScale = Vector3.one;

    [Header("Animation")]
    [Tooltip("Wraps PlayerBase.controller and swaps combat clips. Null = use the base (Big Sword) clips.")]
    public AnimatorOverrideController overrideController;

    [Header("Movement")]
    [Tooltip("Move speed while locked on (strafe/jog pace).")]
    [Min(0.5f)] public float moveSpeed = 5.5f;

    [Header("Dodge clips")]
    public AnimationClip dodgeFront;
    public AnimationClip dodgeBack;
    public AnimationClip dodgeLeft;
    public AnimationClip dodgeRight;
    public AnimationClip backstep;

    [Header("Dodge tuning")]
    [Tooltip("Roll/dart travel distance for directional dodges.")]
    [Min(0.5f)] public float dodgeDistance = 6.5f;
    [Tooltip("Backstep hop (neutral press or backward input): shorter and quicker.")]
    [Min(0.5f)] public float backstepDistance = 3.2f;
    [Min(0.1f)] public float dodgeDuration = 0.5f;
    [Min(0.1f)] public float backstepDuration = 0.32f;
    [Tooltip("Invulnerability window in seconds from dodge start.")]
    [Min(0f)] public float iFrameStart = 0.05f;
    [Min(0f)] public float iFrameEnd = 0.32f;
    [Tooltip("Minimum gap between dodges — prevents spam-teleport.")]
    [Min(0f)] public float dodgeCooldown = 0.12f;
    [Tooltip("Speed multiplier at dodge start/end; eases between them for punch.")]
    [Min(0f)] public float dodgeEntrySpeed = 1.4f;
    [Min(0f)] public float dodgeExitSpeed = 0.7f;

    [Header("Feel")]
    [Tooltip("Draw the dark blade ribbon during plain swings — arts carry their own trail windows.")]
    public bool swingRibbon;
    [Tooltip("Ribbon tip colour; the root and body darken from it. Big sword crimson by default.")]
    public Color ribbonTint = new Color(0.88f, 0.018f, 0.055f);
    [Tooltip("Ribbon body colour (between the root and the bright tip).")]
    public Color ribbonBody = new Color(0.22f, 0.006f, 0.02f);
    [Tooltip("Hit flash / spark colour around the white core. Big sword red by default.")]
    public Color impactTint = new Color(0.95f, 0.12f, 0.14f);
    [Tooltip("Parented to the blade tip while a hit window is open (plain swings only — arts use bladeEdgeFx).")]
    public GameObject edgeFx;
    [Tooltip("Impact burst spawned at the contact point when a plain swing connects.")]
    public GameObject contactFx;
    [Tooltip("SfxBank whoosh id as each hit window opens; missing clip = silent.")]
    public string swingSfx;
    [Tooltip("Carries a contained Crimson Core: while equipped, contacts build Crimson Instability (CrimsonInstability) and Overdrive becomes available.")]
    public bool crimsonCore;

    [Header("Attacks")]
    [Tooltip("Ordered combo chain — each entry is one swing's timing/damage/tracking.")]
    public AttackStep[] attacks;
    [Tooltip("Reach of the swing's hit arc (metres from the player).")]
    [Min(0.5f)] public float attackRange = 2.6f;
    [Tooltip("Full angle of the hit arc, degrees.")]
    [Range(30f, 360f)] public float attackArc = 140f;
}

/// <summary>One swing in a combo chain: timing, hit window, damage, gap-close.</summary>
[System.Serializable]
public sealed class AttackStep
{
    [Tooltip("Reference clip for this swing (the Animator plays the controller's state version).")]
    public AnimationClip clip;
    [Min(0.1f)] public float duration = 0.7f;
    [Tooltip("Seconds into the swing when the hit window opens/closes.")]
    [Min(0f)] public float hitStart = 0.26f;
    [Min(0f)] public float hitEnd = 0.45f;
    [Min(0f)] public float damage = 25f;
    [Tooltip("Posture damage — 0 derives it from damage. Breaks poise-metered enemies (bosses) only when their meter empties.")]
    [Min(0f)] public float poiseDamage;
    [Tooltip("Forward gap-close distance available during the windup.")]
    [Min(0f)] public float stepDistance = 1.2f;
    [Tooltip("Forward stride during the hit window — the clips carry stepping legwork, so the capsule moves with the blow instead of feet sliding.")]
    [Min(0f)] public float advanceDistance = 0.9f;
    [Tooltip("End the attack the frame the capsule grounds — plunge attacks resolve on touchdown instead of posing a dive under the floor.")]
    public bool endOnLand;
    [Tooltip("Root motion drives the capsule during this swing (clip must be a non-inplace take). stepDistance/advanceDistance are ignored.")]
    public bool useRootMotion;
    [Tooltip("Root Y from the clip reaches the capsule — off means code gravity owns descent.")]
    public bool rootMotionY;
    [Tooltip("Stamina spent at commit — aerial strikes pay their own cost. 0 = the flat attack stamina.")]
    [Min(0f)] public float staminaCost;
    [Tooltip("Victim reaction profile for this step's contacts.")]
    public ReactionProfile reaction = ReactionProfile.Normal;
}
