using UnityEngine;

/// <summary>
/// Central, read-only-ish state flags for the player. Heavy/anchored ability states
/// set <see cref="IsRooted"/> while they run; movement-state scripts read it and
/// cede authority. Camera tilt is driven only by displacement states, never by this flag.
/// </summary>
public sealed class PlayerState : MonoBehaviour
{
    [Tooltip("True when the player is locked in place by a heavy swing, cast, or ability.")]
    [SerializeField] private bool isRooted;
    [Tooltip("True when a traversal displacement state (Slide, WallDash, AerialDive) has taken over.")]
    [SerializeField] private bool isDisplacing;
    [Tooltip("True during dodge i-frames — damage and hit reactions must ignore the player.")]
    [SerializeField] private bool isInvulnerable;
    [Tooltip("True while the death sequence owns the player — all controllers must cede input.")]
    [SerializeField] private bool isDead;
    [Tooltip("Committed flask sip — movement slows to half-walk and attacks/dodges/jumps/sprints are rejected without touching IsRooted.")]
    [SerializeField] private bool isDrinking;

    public bool IsRooted
    {
        get => isRooted;
        set
        {
            if (isRooted == value) return;
            isRooted = value;
        }
    }

    public bool IsDisplacing
    {
        get => isDisplacing;
        set
        {
            if (isDisplacing == value) return;
            isDisplacing = value;
        }
    }

    public bool IsInvulnerable
    {
        get => isInvulnerable;
        set
        {
            if (isInvulnerable == value) return;
            isInvulnerable = value;
        }
    }

    public bool IsDead
    {
        get => isDead;
        set
        {
            if (isDead == value) return;
            isDead = value;
        }
    }

    public bool IsDrinking
    {
        get => isDrinking;
        set
        {
            if (isDrinking == value) return;
            isDrinking = value;
        }
    }
}
