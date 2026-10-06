using UnityEngine;

/// <summary>
/// Sockets the active WeaponSet's mesh under a hand bone and applies its
/// AnimatorOverrideController so combat states play this weapon's clips.
/// The weapon IS the combat indicator: it materializes in hand while locked
/// on to an enemy and vanishes the moment lock-on drops.
/// The socket tolerates missing pieces — it only skips what it cannot resolve.
/// </summary>
[DefaultExecutionOrder(80)] // after evaluated animation and FootGrounding; BladeRibbon samples at 100
public sealed class WeaponSocket : MonoBehaviour
{
    [SerializeField] private WeaponSet weaponSet;
    [SerializeField] private WeaponMotionSet motionSet;
    [Tooltip("Hidden copy of the Grruzam skeleton replaying the same states — ik_hand_gun's authored world rotation drives the sword. The humanoid retarget drops that bone on the Hero, so this is the only way to get the real blade arcs.")]
    [SerializeField] private GameObject shadowRig;
    [SerializeField] private HumanBodyBones handBone = HumanBodyBones.RightHand;
    [Tooltip("Scale-in time when the weapon materializes (Genshin summon feel).")]
    [SerializeField, Min(0f)] private float appearDuration = 0.12f;
    [Tooltip("A weapon placed by hand under the hand bone — wins over name-search and spawning, no shadow-rig motion. The WeaponSet's handLocal fields own its grip pose: drags during Play write back to the asset, so they persist.")]
    [SerializeField] private Transform manualWeapon;
    [Tooltip("Where the grip sits between the wrist (0) and the middle-finger knuckle (1). The weapon's pivot is its grip, so ~0.55 puts the handle in the palm instead of at the wrist joint.")]
    [SerializeField, Range(0f, 1f)] private float palmGrip = 0.55f;
    [Tooltip("Editor tuning aid: force the weapon visible (and shadow-driven) even without lock-on, so it can be selected and dragged into place. Untick when done.")]
    [SerializeField] private bool tuneVisible;

    private Animator animator;
    private RuntimeAnimatorController baseController;
    private LockOnController lockOn;
    private PlayerState playerState;
    private static readonly int WeaponDrawHash = Animator.StringToHash("WeaponDraw");
    private GameObject spawnedWeapon;
    private GameObject shadowInstance;
    private Animator shadowAnim;
    private Transform shadowGun;
    private Transform shadowHand;
    private BladePoseResolver.SourcePose lastSourcePose;
    private bool hasSourcePose;

    public WeaponSet Set => weaponSet;
    public GameObject SpawnedWeapon => spawnedWeapon;
    public Transform ActiveBlade => artSword != null ? artSword.transform : spawnedWeapon != null ? spawnedWeapon.transform : null;
    public WeaponSet ActiveBladeSet => artSword != null ? artSwordSet : weaponSet;
    public float PalmGripAmount => palmGrip;
    /// <summary>Changes whenever the visible weapon's identity changes; trails must discard old samples.</summary>
    public int BladePoseRevision { get; private set; }
    public bool IsDrawn => spawnedWeapon != null && spawnedWeapon.activeSelf;
    /// <summary>The shared combat predicate — drives weapon visibility and control-scheme switching.</summary>
    public bool InCombat { get; private set; }
    /// <summary>Actual fight state for resource costs. Drawing/tuning alone is presentation.</summary>
    public bool ResourceCombat => HasCombatThreat(
        lockOn != null && lockOn.IsLockedOn && lockOn.CurrentTarget != null && lockOn.CurrentTarget.IsTargetable,
        EnemyAI.AnyEngaged, BossGolem.AnyEngaged || BossLord.AnyEngaged);

    public static bool HasCombatThreat(bool liveLock, bool enemyEngaged, bool bossEngaged)
        => liveLock || enemyEngaged || bossEngaged;

    private float drawnUntil;
    /// <summary>External hide — the flask stows the blade while a sip runs;
    /// clearing it restores whatever set the normal combat rule would show.</summary>
    public bool Stowed;
    /// <summary>Forces the blade out (and the armed moveset) for a while — weapon
    /// arts used out of combat must not swing an invisible sword.</summary>
    public void KeepDrawn(float seconds) => drawnUntil = Mathf.Max(drawnUntil, Time.time + seconds);

    private GameObject artSword;
    private WeaponSet artSwordSet;
    private float artSwordUntil, artSwordBorn;
    private Vector3 artSwordScale = Vector3.one;
    private readonly System.Collections.Generic.List<Material> artSwordMats
        = new System.Collections.Generic.List<Material>();

    /// <summary>Weapon arts summon a spectral blade for the cast — the set's prefab
    /// under the hand on its authored grip, tinted/emissive gold with a point light
    /// (Maliketh-style magic weapon, not a physical swap), popped in/out by scale.
    /// Named "WeaponSpawned_*" so Equip's placed-weapon scan can never adopt it.
    /// The visible blade keeps its own set's socket corrections, even when a katana is equipped.</summary>
    public void ManifestArtSword(WeaponSet artSet, float seconds, ArtVisualTheme theme = ArtVisualTheme.Original, float scale = 1f)
    {
        if (artSet == null || artSet.weaponPrefab == null || hand == null) return;

        ClearArtSword();
        artSwordSet = artSet;
        artSword = Instantiate(artSet.weaponPrefab, hand);
        artSword.name = "WeaponSpawned_Art";
        ApplySocket(artSword.transform, artSet);
        artSwordScale = artSword.transform.localScale * scale;
        VendorUrp.FixTree(artSword);
        // Spectral gold — cloned materials so the pickup pedestal's copy stays steel.
        foreach (var r in artSword.GetComponentsInChildren<Renderer>(true))
        {
            if (r.sharedMaterial == null) continue;
            var m = new Material(r.sharedMaterial);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_BaseColor", theme == ArtVisualTheme.DarkCrimson ? new Color(0.07f, 0.008f, 0.015f) : new Color(1f, 0.82f, 0.38f));
            m.SetColor("_EmissionColor", theme == ArtVisualTheme.DarkCrimson ? new Color(0.75f, 0.005f, 0.035f) : new Color(1f, 0.62f, 0.12f) * 2.2f);
            r.material = m;
            artSwordMats.Add(m);
        }
        var lt = new GameObject("Glow").AddComponent<Light>();
        lt.transform.SetParent(artSword.transform, false);
        lt.type = LightType.Point;
        lt.color = theme == ArtVisualTheme.DarkCrimson ? new Color(0.8f, 0.015f, 0.04f) : new Color(1f, 0.72f, 0.3f);
        lt.intensity = 1.8f;
        lt.range = 2.6f;
        lt.shadows = LightShadows.None;
        EnsureShadow();
        if (hasSourcePose) ApplyResolvedPose(artSword.transform, artSet, lastSourcePose);
        BladePoseRevision++;
        artSwordBorn = Time.time;
        artSwordUntil = artSwordBorn + Mathf.Max(0.1f, seconds);
        GetComponent<TraversalEffects>()?.WeaponForge(artSword.transform, artSet, true,
            theme == ArtVisualTheme.DarkCrimson ? ArtFx.DarkCrimson : (Color?)null);
    }

    /// <summary>Drops the summoned blade early — cancel, disable, swap.</summary>
    public void ClearArtSword()
    {
        artSwordSet = null;
        for (var i = 0; i < artSwordMats.Count; i++)
            if (artSwordMats[i] != null) Destroy(artSwordMats[i]);
        artSwordMats.Clear();
        if (artSword == null) return;
        // Dissolve where it stood (the forge keeps world endpoints once the blade is destroyed).
        if (Application.isPlaying) GetComponent<TraversalEffects>()?.WeaponForge(artSword.transform, null, false, ArtFx.DarkCrimson);
        artSword.SetActive(false);
        Destroy(artSword);
        artSword = null;
        BladePoseRevision++;
    }

    private Transform hand;
    private Transform knuckle;
    private float appearT = 1f;

    /// <summary>Palm centre: the grip point the weapon's pivot should sit on.</summary>
    private Vector3 PalmPosition => BladePoseResolver.Palm(hand, knuckle, palmGrip);

    private void Awake()
    {
        animator = FindValidAnimator();
        if (animator != null) baseController = animator.runtimeAnimatorController;
        lockOn = GetComponent<LockOnController>();
        playerState = GetComponent<PlayerState>();
        if (animator != null)
        {
            hand = animator.GetBoneTransform(handBone);
            knuckle = animator.GetBoneTransform(handBone == HumanBodyBones.LeftHand
                ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
        }
        if (weaponSet != null)
            Equip(weaponSet);
    }

    private void Update()
    {
        if (artSword != null && Time.time >= artSwordUntil) ClearArtSword();
        if (spawnedWeapon == null || weaponSet == null || hand == null)
            return;

        // The weapon IS the combat indicator: drawn while locked on to a live
        // target OR while any enemy is aggroed on the player — sheathed otherwise.
        // tuneVisible forces combat too: the armed idle hand pose is the one
        // worth tuning the grip against.
        InCombat = ResourceCombat
                   || tuneVisible
                   || Time.time < drawnUntil;
        var shown = (InCombat || tuneVisible) && artSword == null && !Stowed;
        if (spawnedWeapon.activeSelf != shown)
        {
            // Blade forge covers the hard SetActive cut both ways: scan + gathering
            // shards on draw, receding edge + breaking shards on sheathe.
            GetComponent<TraversalEffects>()?.WeaponForge(spawnedWeapon.transform, weaponSet, shown);
            spawnedWeapon.SetActive(shown);
            if (shown && InCombat)
            {
                appearT = 0f;
                // Snap, don't glide in from wherever the pose was when hidden.
                shadowPoseApplied = false;
                TryPlayDrawFlourish();
            }
            else if (!shown)
            {
                animator.ResetTrigger(WeaponDrawHash);
            }
        }

        var t = spawnedWeapon.transform;
        // handLocal* on the asset owns the grip pose for spawned AND placed
        // weapons — the asset is what survives Play Mode, so scene-view drags
        // write back to it and re-apply next session. Only meaningful for a
        // weapon directly parented to the hand bone (handLocal is hand-space).
        var handParented = manualWeapon == null || t.parent == hand;
        if (handParented && SocketValuesChanged(weaponSet))
        {
            ApplySocket(t, weaponSet);
            CacheApplied(t, weaponSet);
        }
        // motionSet/shadowGun only re-pose SPAWNED weapons — a placed weapon is
        // never driven, so for it a transform change is always a user edit.
        else if (handParented && socketApplied && (InCombat || tuneVisible) && appearT >= 1f
                 && (manualWeapon != null || (motionSet == null && shadowGun == null))
                 && TransformEdited(t))
        {
            // The weapon was moved/rotated by hand in the Scene view during Play
            // Mode — write the tweak back to the asset so it persists. The euler
            // captures the full rotation, so any blade-roll folds into it.
            var boneScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            weaponSet.handLocalPosition = t.localPosition;
            weaponSet.handLocalEuler = t.localEulerAngles;
            weaponSet.bladeRoll = 0f;
            weaponSet.handLocalScale = Vector3.Scale(t.localScale, boneScale);
            CacheApplied(t, weaponSet);
            Debug.Log($"[WeaponSocket] saved grip pose -> {weaponSet.name}", weaponSet);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(weaponSet);
#endif
        }

        if (manualWeapon == null)
        {
            // Materialize scale-in: 60% → 100% over appearDuration.
            if (InCombat && appearT >= 1f && motionSet != null && shadowGun == null)
                ApplyBakedMotion(t);
        }

        var ck = CrimsonK(weaponSet);
        if ((InCombat && appearT < 1f) || ck != 1f || appliedCrimson != 1f)
        {
            if (InCombat && appearT < 1f)
                appearT = appearDuration > 0f ? Mathf.Min(1f, appearT + Time.deltaTime / appearDuration) : 1f;
            t.localScale = appliedLocalScale * (InCombat ? Mathf.Lerp(0.6f, 1f, appearT) : 1f) * ck;
            appliedCrimson = ck;
        }

        // Pose is resolved in LateUpdate from the art's own set, never copied
        // from an equipped weapon with different socket corrections.
        if (artSword != null)
        {
            var grow = Mathf.Clamp01((Time.time - artSwordBorn) / 0.12f);
            var shrink = Mathf.Clamp01((artSwordUntil - Time.time) / 0.18f);
            artSword.transform.localScale = artSwordScale * Mathf.Min(grow, shrink) * CrimsonK(artSwordSet);
        }
    }

    /// <summary>
    /// Lock-on while planted plays the WeaponDraw flourish (the Grruzam Intro —
    /// the pack's draw-the-blade entrance). While moving/sliding/dodging the
    /// weapon just materializes; a mid-swing plant interrupt would look broken.
    /// </summary>
    private void TryPlayDrawFlourish()
    {
        if (playerState != null && (playerState.IsDisplacing || playerState.IsRooted)) return;
        if (animator.GetFloat("Speed") > 0.05f) return;
        animator.SetTrigger(WeaponDrawHash);
    }

    private Quaternion appliedLocalRot;
    private Vector3 appliedLocalPos;
    private bool shadowPoseApplied;

    /// <summary>
    /// After animation and grounding, sample the shadow's current/next states
    /// independently and map the authored bone rotation into the Hero's frame.
    /// Each blade anchors at the palm with its own set's corrections.
    /// </summary>
    private void LateUpdate()
    {
        if (!(InCombat || tuneVisible) || shadowAnim == null || shadowGun == null || shadowHand == null || hand == null || animator == null)
            return;

        shadowAnim.SetFloat("Speed", animator.GetFloat("Speed"));
        shadowAnim.SetFloat("MoveX", animator.GetFloat("MoveX"));
        shadowAnim.SetFloat("MoveY", animator.GetFloat("MoveY"));
        shadowAnim.SetBool("Locked", animator.GetBool("Locked"));
        shadowAnim.SetBool("Sprinting", animator.GetBool("Sprinting"));

        var info = animator.GetCurrentAnimatorStateInfo(0);
        if (!TrySampleShadowPose(info.fullPathHash, info.normalizedTime, out var source)) return;
        if (animator.IsInTransition(0))
        {
            // Current and next hashes each use THEIR normalized time. A cached
            // incoming hash paired with outgoing time desynchronizes every later blend frame.
            var next = animator.GetNextAnimatorStateInfo(0);
            var w = Mathf.Clamp01(animator.GetAnimatorTransitionInfo(0).normalizedTime);
            if (TrySampleShadowPose(next.fullPathHash, next.normalizedTime, out var incoming))
                source = BladePoseResolver.Blend(source, incoming, w);
        }
        lastSourcePose = source;
        hasSourcePose = true;
        if (manualWeapon == null && spawnedWeapon != null && weaponSet != null)
            ApplyEquippedPose(source);
        if (artSword != null && artSwordSet != null)
            ApplyResolvedPose(artSword.transform, artSwordSet, source);
    }

    private void ApplyEquippedPose(BladePoseResolver.SourcePose source)
    {
        var t = spawnedWeapon.transform;
        var pose = BladePoseResolver.Resolve(source, animator.transform.rotation, PalmPosition, hand.rotation, weaponSet);
        var targetPos = pose.position;
        var targetRot = pose.rotation;
        var roll = Quaternion.AngleAxis(weaponSet.bladeRoll, BladePoseResolver.BladeAxis(weaponSet));
        var authoredRotation = animator.transform.rotation * source.rotation;
        var anchor = PalmPosition + animator.transform.rotation * source.handToSocket * weaponSet.socketDeltaScale;

        // Scene-view live tune: moving/rotating the weapon during Play persists
        // the difference back into the offset fields on the asset. Compared in
        // LOCAL space and only while the weapon is selected — the weapon rides
        // the animated hand, so its world pose changes every frame on its own;
        // a world-space check read that motion as a hand edit and wrote garbage
        // into weaponRotOffset each frame (the drifting blade facing).
        var liveTune = false;
#if UNITY_EDITOR
        liveTune = UnityEditor.Selection.activeTransform == t;
#endif
        var rotEdited = liveTune && shadowPoseApplied && t.localRotation != appliedLocalRot;
        var posEdited = liveTune && shadowPoseApplied && (t.localPosition - appliedLocalPos).sqrMagnitude > 1e-10f;
        if (rotEdited || posEdited)
        {
            if (rotEdited)
            {
                weaponSet.weaponRotOffset =
                    (Quaternion.Inverse(authoredRotation) * t.rotation * Quaternion.Inverse(roll)).eulerAngles;
                targetRot = t.rotation;
            }
            if (posEdited)
            {
                weaponSet.weaponPosOffset =
                    Quaternion.Inverse(hand.rotation) * (t.position - anchor);
                targetPos = t.position;
            }
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(weaponSet);
#endif
        }

        // Animator crossfades already blend the pose. Extra filtering makes
        // accelerated swings trail behind the evaluated palm and authored arc.
        t.SetPositionAndRotation(targetPos, targetRot);
        appliedLocalPos = t.localPosition;
        appliedLocalRot = t.localRotation;
        shadowPoseApplied = true;
    }

    private void ApplyResolvedPose(Transform blade, WeaponSet set, BladePoseResolver.SourcePose source)
    {
        var pose = BladePoseResolver.Resolve(source, animator.transform.rotation, PalmPosition, hand.rotation, set);
        blade.SetPositionAndRotation(pose.position, pose.rotation);
    }

    /// <summary>Scrubs the shadow rig to a state + normalized time and reads
    /// the socket/hand world transforms at that exact authored pose.</summary>
    private bool TrySampleShadowPose(int stateHash, float normalizedTime, out BladePoseResolver.SourcePose source)
    {
        source = default;
        if (!shadowAnim.HasState(0, stateHash)) return false;
        shadowAnim.Play(stateHash, 0, normalizedTime);
        shadowAnim.Update(0f);
        source = BladePoseResolver.Capture(shadowInstance.transform.rotation, shadowGun.position, shadowGun.rotation, shadowHand.position);
        return true;
    }

    private readonly System.Collections.Generic.List<AnimatorClipInfo> clipInfos
        = new System.Collections.Generic.List<AnimatorClipInfo>(8);

    /// <summary>
    /// Replays the source rig's weapon-socket (ik_hand_gun) motion that the
    /// humanoid retarget drops. Values are baked into Hero-hand local space by
    /// WeaponMotionSet — weight-blended across the animator's active clips so
    /// blend trees and crossfades stay coherent.
    /// </summary>
    private void ApplyBakedMotion(Transform weapon)
    {
        var info = animator.GetCurrentAnimatorStateInfo(0);
        var nt = info.normalizedTime;
        clipInfos.Clear();
        animator.GetCurrentAnimatorClipInfo(0, clipInfos);

        var pos = Vector3.zero;
        var rot = Quaternion.identity;
        var wSum = 0f;
        var haveRot = false;
        foreach (var ci in clipInfos)
        {
            var e = motionSet.Find(ci.clip != null ? ci.clip.name : null);
            if (e == null || ci.weight <= 0f) continue;
            motionSet.Sample(e, nt, out var p, out var r);
            wSum += ci.weight;
            pos = Vector3.LerpUnclamped(pos, p, ci.weight / wSum);
            rot = haveRot ? Quaternion.Slerp(rot, r, ci.weight / wSum) : r;
            haveRot = true;
        }

        if (!haveRot)
        {
            pos = motionSet.bindPos;
            rot = motionSet.bindRot;
        }

        var axis = weaponSet.bladeAxis.sqrMagnitude > 0.001f ? weaponSet.bladeAxis.normalized : Vector3.forward;
        weapon.localPosition = pos;
        weapon.localRotation = rot * Quaternion.AngleAxis(weaponSet.bladeRoll, axis);
    }

    private bool socketApplied;
    private Vector3 appliedPos, appliedEuler, appliedAxis, appliedHandScale;
    private float appliedRoll;
    private Quaternion appliedRot;
    private Vector3 appliedLocalScale;

    private bool SocketValuesChanged(WeaponSet set)
    {
        return !socketApplied
            || appliedPos != set.handLocalPosition
            || appliedEuler != set.handLocalEuler
            || appliedRoll != set.bladeRoll
            || appliedAxis != set.bladeAxis
            || appliedHandScale != set.handLocalScale;
    }

    private bool TransformEdited(Transform t)
    {
        return t.localPosition != appliedPos
            || t.localRotation != appliedRot
            || t.localScale != appliedLocalScale * appliedCrimson; // Overdrive growth is ours, not a hand edit
    }

    /// <summary>Visual growth multiplier for crimson-core blades (equipped or
    /// summoned) — CrimsonInstability's Overdrive. 1 = authored size. Applied
    /// on top of the socket scale, never written back to the WeaponSet.</summary>
    [System.NonSerialized] public float CrimsonScale = 1f;
    private float appliedCrimson = 1f;
    private float CrimsonK(WeaponSet s) => s != null && s.crimsonCore ? CrimsonScale : 1f;

    private void CacheApplied(Transform t, WeaponSet set)
    {
        socketApplied = true;
        appliedPos = set.handLocalPosition;
        appliedEuler = set.handLocalEuler;
        appliedRoll = set.bladeRoll;
        appliedAxis = set.bladeAxis;
        appliedHandScale = set.handLocalScale;
        appliedRot = t.localRotation;
        appliedLocalScale = t.localScale;
    }

    /// <summary>No stored grip pose yet — a never-tuned set seeds itself from
    /// the placed weapon's scene transform instead of stomping it with zeros.</summary>
    private static bool PoseUnset(WeaponSet set) =>
        set.handLocalPosition == Vector3.zero
        && set.handLocalEuler == Vector3.zero
        && set.bladeRoll == 0f
        && set.handLocalScale == Vector3.one;

    public void Equip(WeaponSet set)
    {
        ClearArtSword();
        hasSourcePose = false;
        shadowPoseApplied = false;
        BladePoseRevision++;
        weaponSet = set;
        if (set != null) GetComponent<Inventory>()?.Own(set); // every equip path registers ownership
        if (animator == null || set == null)
        {
            if (spawnedWeapon != null && (manualWeapon == null || spawnedWeapon != manualWeapon.gameObject))
                Destroy(spawnedWeapon);
            spawnedWeapon = null;
            return;
        }

        if (hand == null)
        {
            Debug.LogWarning("[WeaponSocket] hand bone not found on the humanoid rig.", this);
        }
        else
        {
            // A weapon placed under the hand bone by hand wins over spawning:
            // its local transform IS the authored grip — we adopt it and only
            // drive visibility. With several placed weapons (katana, claymore)
            // the one matching this set's prefab/name-hint is adopted; every
            // other placed weapon is hidden so the wrong blade never shows.
            // Search BEFORE destroying: Destroy() is deferred to end of frame,
            // so the old spawn's corpse was still searchable and got re-adopted —
            // then died that frame, leaving spawnedWeapon permanently null.
            var candidates = new System.Collections.Generic.List<Transform>();
            CollectPlacedWeapons(hand, set, shadowInstance, candidates);
            CollectPlacedWeapons(transform, set, shadowInstance, candidates);
            var placed = MatchPlaced(candidates, set)
                      ?? manualWeapon
                      ?? (candidates.Count > 0 ? candidates[0] : null);
            foreach (var c in candidates)
                if (c != placed && c.gameObject.activeSelf) c.gameObject.SetActive(false);
            if (spawnedWeapon != null
                && (placed == null || spawnedWeapon != placed.gameObject)
                && (manualWeapon == null || spawnedWeapon != manualWeapon.gameObject))
                Destroy(spawnedWeapon);
            spawnedWeapon = null;
            if (placed != null)
            {
                manualWeapon = placed;
                spawnedWeapon = placed.gameObject;
                // Hand-space pose storage needs the weapon directly under the
                // hand bone — reparent anything else, keeping its authored
                // world pose, instead of silently skipping persistence.
                if (placed.parent != hand)
                    placed.SetParent(hand, true);
                // The asset owns the pose: a never-tuned set seeds itself from
                // the authored scene placement; once set, stored values win so
                // Play-Mode tweaks survive exiting Play.
                if (PoseUnset(set))
                {
                    var boneScale = placed.parent.lossyScale;
                    set.handLocalPosition = placed.localPosition;
                    set.handLocalEuler = placed.localEulerAngles;
                    set.bladeRoll = 0f;
                    set.handLocalScale = Vector3.Scale(placed.localScale, boneScale);
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(set);
#endif
                }
                else
                {
                    ApplySocket(placed, set);
                }
                CacheApplied(placed, set);
                Debug.Log($"[WeaponSocket] adopted hand-placed weapon '{placed.name}'", placed);
            }
            else if (set.weaponPrefab != null)
            {
                spawnedWeapon = Instantiate(set.weaponPrefab, hand);
                // Neutral name: FindPlacedWeapon must never adopt a spawned clone
                // as a "hand-placed" weapon — that sets manualWeapon and skips the
                // shadow rig that supplies authored blade arcs.
                spawnedWeapon.name = "WeaponSpawned";
                ApplySocket(spawnedWeapon.transform, set);
                CacheApplied(spawnedWeapon.transform, set);
            }
            if (spawnedWeapon != null)
            {
                InCombat = (lockOn != null && lockOn.IsLockedOn) || EnemyAI.AnyEngaged || BossGolem.AnyEngaged || BossLord.AnyEngaged || tuneVisible;
                spawnedWeapon.SetActive(InCombat);
                if (InCombat) appearT = 0f;
                // The vendor weapon material predates URP (grey/magenta) — give
                // it a steel look.
                VendorUrp.FixTree(spawnedWeapon);
            }
        }

        // Null override = the base (Big Sword) clips — restore the controller
        // cached at Awake so switching weapons never leaves a stale override.
        animator.runtimeAnimatorController =
            set.overrideController != null ? set.overrideController : baseController;
        Debug.Log($"[WeaponSocket] Equip {set.displayName}: animator '{animator.name}' controller -> {animator.runtimeAnimatorController.name}", this);

        // The shadow rig only exists to drive the spawned weapon's world pose —
        // a hand-placed weapon follows the bone directly and doesn't need it.
        if (manualWeapon == null)
        {
            if (shadowAnim != null)
                shadowAnim.runtimeAnimatorController = animator.runtimeAnimatorController;
            else
                EnsureShadow();
        }
    }

    /// <summary>Dev switching for hand-placement tuning: assign a WeaponSet in
    /// the inspector, then right-click the component header and choose this.</summary>
    [ContextMenu("Equip Assigned WeaponSet")]
    private void DevEquip() => Equip(weaponSet);

    /// <summary>
    /// Collects weapon meshes already placed under the bone by hand — matches
    /// vendor/weapon-name conventions (SM_Wep*, sword, katana, claymore, blade)
    /// plus this set's prefab name. Spawned clones (WeaponSpawned) and the
    /// shadow rig's weapon bones are excluded.
    /// </summary>
    private static void CollectPlacedWeapons(Transform root, WeaponSet set, GameObject shadow,
        System.Collections.Generic.List<Transform> found)
    {
        if (root == null) return;
        var prefabName = set.weaponPrefab != null ? set.weaponPrefab.name : null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root || t == root.root || found.Contains(t)) continue;
            // Skip the hidden shadow rig — its weapon bones would false-positive.
            if (shadow != null && t.IsChildOf(shadow.transform)) continue;
            var n = t.name;
            if (n.IndexOf("WeaponSpawned", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (prefabName != null && (n == prefabName || n == prefabName + "(Clone)")) { found.Add(t); continue; }
            if (n.IndexOf("SM_Wep", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("sword", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("katana", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("claymore", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("greatsword", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("blade", System.StringComparison.OrdinalIgnoreCase) >= 0)
                found.Add(t);
        }
    }

    /// <summary>Picks the placed weapon belonging to this set: prefab-name
    /// match first, then the set's socket name hint.</summary>
    private static Transform MatchPlaced(System.Collections.Generic.List<Transform> candidates, WeaponSet set)
    {
        var prefabName = set.weaponPrefab != null ? set.weaponPrefab.name : null;
        if (prefabName != null)
        {
            var m = candidates.Find(t => t.name.Equals(prefabName, System.StringComparison.OrdinalIgnoreCase)
                                      || t.name.StartsWith(prefabName, System.StringComparison.OrdinalIgnoreCase));
            if (m != null) return m;
        }
        if (!string.IsNullOrEmpty(set.socketNameHint))
            return candidates.Find(t => t.name.IndexOf(set.socketNameHint, System.StringComparison.OrdinalIgnoreCase) >= 0);
        return null;
    }

    /// <summary>
    /// Spawns the hidden Grruzam skeleton once. Its Animator replays the same
    /// controller (same state hashes) on its own humanoid avatar — where the
    /// ik_hand_gun weapon-bone curves that our Hero retarget drops still play,
    /// because the bone paths actually exist on this rig.
    /// </summary>
    private void EnsureShadow()
    {
        if (shadowInstance != null || shadowRig == null || animator == null)
            return;
        shadowInstance = Instantiate(shadowRig, transform);
        shadowInstance.name = "_WeaponShadow";
        // The socket delta transfers in world space — the shadow must share the
        // player's exact frame or the offset direction rotates off-axis. Scale
        // stays authored (it's part of the rig's proportions).
        shadowInstance.transform.localPosition = Vector3.zero;
        shadowInstance.transform.localRotation = Quaternion.identity;
        foreach (var r in shadowInstance.GetComponentsInChildren<Renderer>(true))
            r.enabled = false;
        foreach (var mb in shadowInstance.GetComponentsInChildren<MonoBehaviour>(true))
            mb.enabled = false;
        shadowAnim = shadowInstance.GetComponentInChildren<Animator>();
        if (shadowAnim == null)
        {
            Destroy(shadowInstance);
            shadowInstance = null;
            return;
        }
        shadowAnim.runtimeAnimatorController = animator.runtimeAnimatorController;
        shadowAnim.applyRootMotion = false;
        // Renderers are off, so transform culling would freeze the skeleton —
        // AlwaysAnimate keeps ik_hand_gun evaluating.
        shadowAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        shadowGun = FindDeep(shadowInstance.transform, "ik_hand_gun");
        shadowHand = FindDeep(shadowInstance.transform, "hand_r");
        if (shadowGun == null || shadowHand == null)
            Debug.LogWarning("[WeaponSocket] shadow rig is missing ik_hand_gun/hand_r bones.", this);
    }

    private void OnDisable() => ClearArtSword();

    private void OnDestroy()
    {
        if (shadowInstance != null)
            Destroy(shadowInstance);
    }

    /// <summary>SetDirty only marks the asset — flush tuned grip poses to disk
    /// when Play stops so a tune survives an editor close without a manual save.</summary>
    private void OnApplicationQuit()
    {
#if UNITY_EDITOR
        if (weaponSet != null) UnityEditor.AssetDatabase.SaveAssetIfDirty(weaponSet);
#endif
    }

    private static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }

    private static void ApplySocket(Transform weapon, WeaponSet set)
    {
        weapon.localPosition = set.handLocalPosition;
        // Base grip rotation first, then bladeRoll around the blade axis (handle→tip) —
        // post-multiplying keeps the roll in sword space, not bone space.
        var axis = set.bladeAxis.sqrMagnitude > 0.001f ? set.bladeAxis.normalized : Vector3.forward;
        weapon.localRotation = Quaternion.Euler(set.handLocalEuler) * Quaternion.AngleAxis(set.bladeRoll, axis);
        // handLocalScale is world-scale intent — the rig's bones may be scaled
        // (e.g. 0.01), so compensate or the weapon inherits the bone's scale.
        var boneScale = weapon.parent != null ? weapon.parent.lossyScale : Vector3.one;
        weapon.localScale = BladePoseResolver.LocalScale(set.handLocalScale, boneScale);
    }

    private void OnDrawGizmos()
    {
        // Scene-view axes for the hand bone: red = +X, green = +Y, blue = +Z.
        // Helps decide which euler rotation aligns the blade with the grip.
        var bone = hand;
        if (bone == null)
        {
            var anim = animator != null ? animator : FindValidAnimator();
            if (anim != null) bone = anim.GetBoneTransform(handBone);
        }
        if (bone == null) return;
        var len = 0.12f;
        Gizmos.color = Color.red;
        Gizmos.DrawRay(bone.position, bone.right * len);
        Gizmos.color = Color.green;
        Gizmos.DrawRay(bone.position, bone.up * len);
        Gizmos.color = Color.blue;
        Gizmos.DrawRay(bone.position, bone.forward * len);
    }

    private Animator FindValidAnimator()
    {
        foreach (var a in GetComponentsInChildren<Animator>(true))
        {
            if (a != null && a.enabled && a.runtimeAnimatorController != null && a.avatar != null && a.avatar.isHuman)
                return a;
        }
        return null;
    }
}
