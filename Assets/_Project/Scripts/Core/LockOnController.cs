using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

/// <summary>
/// Souls-style lock-on. Middle mouse / right-stick press toggles: acquires the
/// candidate nearest the camera's view centre that has line of sight, or clears
/// the lock. While locked, a horizontal look flick (stick tilt or fast mouse
/// swipe) switches to the next best target on that side of the screen. The lock
/// auto-releases when the target dies/untargets, leaves the unlock radius, or
/// stays occluded past a grace period. A ring-and-pip sigil marks the aim point.
///
/// Input is a runtime-created action plus direct device reads for flicks, so this
/// component needs no InputActionAsset wiring — drop it on the player.
/// </summary>
[DefaultExecutionOrder(10)]
public sealed class LockOnController : MonoBehaviour
{
    [Header("Acquisition")]
    [Tooltip("Max distance at which a target can be locked.")]
    [SerializeField, Min(1f)] private float lockRadius = 30f;
    [Tooltip("Field-of-view cone (degrees) around the camera forward used for acquisition.")]
    [SerializeField, Range(30f, 170f)] private float lockConeAngle = 140f;
    [Tooltip("How much distance counts against a candidate relative to view angle. Higher = prefer nearer targets over more centered ones.")]
    [SerializeField, Min(0f)] private float distanceScoreWeight = 1.5f;
    [Tooltip("Ray origin height above the player pivot used for line-of-sight checks.")]
    [SerializeField, Min(0f)] private float losChestHeight = 1.3f;
    [Tooltip("Layers considered for line-of-sight blockers and target colliders.")]
    [SerializeField] private LayerMask losMask = ~0;

    [Header("Retention")]
    [Tooltip("Distance at which a held lock is released.")]
    [SerializeField, Min(1f)] private float unlockRadius = 35f;
    [Tooltip("Seconds the target may stay occluded before the lock drops.")]
    [SerializeField, Min(0f)] private float losGracePeriod = 1.5f;

    [Header("Switching")]
    [Tooltip("Stick tilt (0-1) that counts as a target-switch flick.")]
    [SerializeField, Range(0.3f, 1f)] private float stickFlickThreshold = 0.7f;
    [Tooltip("Mouse delta in pixels per frame that counts as a target-switch flick.")]
    [SerializeField, Min(1f)] private float mouseFlickPixels = 15f;
    [Tooltip("Minimum time between target switches.")]
    [SerializeField, Min(0.05f)] private float switchCooldown = 0.4f;
    [Tooltip("Viewport-space deadzone around the current target; candidates inside it are not switch options.")]
    [SerializeField, Range(0f, 0.3f)] private float switchDeadzone = 0.08f;

    [Header("Events")]
    [SerializeField] private UnityEvent onLockAcquired;
    [SerializeField] private UnityEvent onLockReleased;

    private InputAction lockAction;
    private Targetable currentTarget;
    private float losLostTimer;
    private float nextSwitchTime;
    private HashSet<Collider> playerColliders;
    private PlayerState state;

    public bool IsLockedOn => currentTarget != null;
    public Targetable CurrentTarget => currentTarget;
    public Vector3 AimPosition => currentTarget != null ? currentTarget.AimPosition : transform.position + Vector3.up * losChestHeight;

    private void Awake()
    {
        lockAction = new InputAction("LockOn", InputActionType.Button);
        lockAction.AddBinding("<Mouse>/middleButton");
        lockAction.AddBinding("<Gamepad>/rightStickPress");
        playerColliders = new HashSet<Collider>(GetComponentsInChildren<Collider>(true));
        state = GetComponent<PlayerState>();
    }

    private void OnEnable() => lockAction.Enable();

    private void OnDisable()
    {
        lockAction.Disable();
        if (currentTarget != null)
            ClearLock();
    }

    private void OnDestroy() => lockAction.Dispose();

    /// <summary>
    /// Rebinds the lock toggle. Empty paths keep the default binding for that device.
    /// </summary>
    public void SetLockOnBinding(string keyboardPath, string gamepadPath = "<Gamepad>/rightStickPress")
    {
        if (lockAction == null) return;
        lockAction.Disable();
        if (lockAction.bindings.Count > 0 && !string.IsNullOrEmpty(keyboardPath))
            lockAction.ApplyBindingOverride(0, keyboardPath);
        if (lockAction.bindings.Count > 1 && !string.IsNullOrEmpty(gamepadPath))
            lockAction.ApplyBindingOverride(1, gamepadPath);
        if (enabled) lockAction.Enable();
    }

    private void Update()
    {
        if (state != null && state.IsDead)
        {
            if (currentTarget != null) ClearLock();
            return;
        }
        if (lockAction != null && lockAction.WasPressedThisFrame())
            ToggleLock();

        if (currentTarget == null) return;

        ValidateTarget(Time.deltaTime);
        if (currentTarget != null)
            ReadSwitchFlick();
    }

    private void ToggleLock()
    {
        if (currentTarget != null)
        {
            ClearLock();
            return;
        }

        var best = FindBestCandidate(Vector2.zero, null);
        if (best != null)
            SetLock(best);
        else
            Debug.Log("[LockOn] toggle pressed; no valid candidate in range/cone/LOS.", this);
    }

    /// <summary>
    /// Best candidate for acquisition, or for switching toward a screen direction
    /// (switchDir.x &gt; 0 = right of current target, &lt; 0 = left). current excluded.
    /// </summary>
    private Targetable FindBestCandidate(Vector2 switchDir, Targetable exclude)
    {
        var cam = Camera.main;
        var forward = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : transform.forward;
        if (forward.sqrMagnitude < 0.001f) forward = transform.forward;
        var origin = transform.position + Vector3.up * losChestHeight;

        var currentViewport = Vector3.zero;
        var hasCurrentViewport = false;
        if (exclude != null && cam != null)
        {
            currentViewport = cam.WorldToViewportPoint(exclude.AimPosition);
            hasCurrentViewport = currentViewport.z > 0f;
        }

        var hits = Physics.OverlapSphere(origin, lockRadius, ~0, QueryTriggerInteraction.Ignore);
        Targetable best = null;
        var bestScore = float.MaxValue;

        foreach (var hit in hits)
        {
            if (hit == null || playerColliders.Contains(hit)) continue;
            var t = hit.GetComponentInParent<Targetable>();
            if (t == null || !t.IsTargetable || t == exclude) continue;

            if (switchDir.x != 0f && hasCurrentViewport)
            {
                // Flick switch: candidate must sit on the flicked side of the current
                // target in screen space, and in front of the camera.
                var vp = cam.WorldToViewportPoint(t.AimPosition);
                if (vp.z <= 0f) continue;
                var dx = vp.x - currentViewport.x;
                if (switchDir.x > 0f ? dx <= switchDeadzone : dx >= -switchDeadzone) continue;
                var score = Mathf.Abs(dx) + Mathf.Abs(vp.y - currentViewport.y) * 0.5f;
                if (score < bestScore && HasLineOfSight(t))
                {
                    bestScore = score;
                    best = t;
                }
                continue;
            }

            var toTarget = Vector3.ProjectOnPlane(t.AimPosition - transform.position, Vector3.up);
            // Directly above/below has no meaningful planar direction — treat as dead ahead.
            var angle = toTarget.sqrMagnitude < 0.05f ? 0f : Vector3.Angle(forward, toTarget);
            if (angle > lockConeAngle * 0.5f) continue;
            if (!HasLineOfSight(t)) continue;

            var dist = toTarget.magnitude;
            var candidateScore = angle + dist * distanceScoreWeight;
            if (candidateScore < bestScore)
            {
                bestScore = candidateScore;
                best = t;
            }
        }

        return best;
    }

    /// <summary>
    /// True when the first non-player collider on the chest→aim ray is part of the
    /// target (or nothing is hit). Any other collider in between blocks the lock.
    /// </summary>
    private bool HasLineOfSight(Targetable t)
    {
        var origin = transform.position + Vector3.up * losChestHeight;
        var to = t.AimPosition - origin;
        var dist = to.magnitude;
        if (dist < 0.05f) return true;

        var hits = Physics.RaycastAll(origin, to / dist, dist - 0.05f, losMask, QueryTriggerInteraction.Ignore);
        RaycastHit? first = null;
        var firstDist = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider == null || playerColliders.Contains(h.collider)) continue;
            if (h.distance < firstDist)
            {
                firstDist = h.distance;
                first = h;
            }
        }

        if (first == null) return true;
        return first.Value.collider.GetComponentInParent<Targetable>() == t;
    }

    private void ValidateTarget(float dt)
    {
        if (currentTarget == null || !currentTarget.IsTargetable)
        {
            // A killed lock retargets to the best remaining candidate instead
            // of dropping — same acquisition rule (view centre + distance +
            // LOS), the corpse excluded. Nothing valid in sight → release.
            var dead = currentTarget;
            currentTarget = null;
            var next = FindBestCandidate(Vector2.zero, dead);
            if (next != null) SetLock(next);
            else ClearLock();
            return;
        }

        var dist = Vector3.Distance(transform.position, currentTarget.transform.position);
        if (dist > unlockRadius)
        {
            ClearLock();
            return;
        }

        if (HasLineOfSight(currentTarget))
        {
            losLostTimer = 0f;
        }
        else
        {
            losLostTimer += dt;
            if (losLostTimer >= losGracePeriod)
            {
                Debug.Log("[LockOn] target occluded past grace period; releasing.", this);
                ClearLock();
            }
        }
    }

    private void ReadSwitchFlick()
    {
        if (Time.time < nextSwitchTime) return;

        var dir = 0f;
        if (Gamepad.current != null)
        {
            var x = Gamepad.current.rightStick.ReadValue().x;
            if (Mathf.Abs(x) >= stickFlickThreshold) dir = Mathf.Sign(x);
        }
        if (Mouse.current != null)
        {
            var dx = Mouse.current.delta.ReadValue().x;
            if (Mathf.Abs(dx) >= mouseFlickPixels) dir = Mathf.Sign(dx);
        }
        if (dir == 0f) return;

        var next = FindBestCandidate(new Vector2(dir, 0f), currentTarget);
        nextSwitchTime = Time.time + switchCooldown;
        if (next != null)
            SetLock(next);
    }

    private void SetLock(Targetable t)
    {
        currentTarget = t;
        losLostTimer = 0f;
        GameHud.Ensure(); // the HUD draws the lock-on reticle
        onLockAcquired?.Invoke();
        Debug.Log($"[LockOn] locked '{t.name}'", t);
    }

    private void ClearLock()
    {
        if (currentTarget == null) return;
        currentTarget = null;
        losLostTimer = 0f;
        onLockReleased?.Invoke();
        Debug.Log("[LockOn] released", this);
    }
}
