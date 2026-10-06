using UnityEngine;

/// <summary>
/// Player-side damage pool — the counterpart to the dummies' Health.
/// Respects dodge i-frames via PlayerState.IsInvulnerable plus its own
/// post-hit/respawn grace window, shakes the camera on connect, and on death
/// hands off to <see cref="GameLoop"/> — the full YOU DIED → drop souls →
/// respawn-at-bonfire sequence, not an instant reset.
/// </summary>
public sealed class PlayerHealth : MonoBehaviour
{
    [SerializeField, Min(1f)] private float maxHealth = 100f;
    [Tooltip("Grace period after taking a hit and after respawning — damage is ignored.")]
    [SerializeField, Min(0f)] private float graceSeconds = 0.8f;
    [SerializeField, Min(0f)] private float hitShake = 0.35f;
    [SerializeField, Min(0f)] private float deathShake = 0.8f;

    public float Current { get; private set; }
    public float Max => maxHealth;
    public bool IsDead { get; private set; }
    /// <summary>Own grace timer — enemies OR this with PlayerState.IsInvulnerable.</summary>
    public bool Invulnerable => graceT > 0f;
    /// <summary>Fired on every connect — HUD hooks this for the health bar.</summary>
    public event System.Action<float, float> Damaged; // (current, max)
    public event System.Action Died;
    /// <summary>Fired by Revive — the true respawn moment (checkpoint teleports
    /// the player). HUD waits on this, not on the health refill, so the
    /// shattered heart stays broken until the body is back.</summary>
    public event System.Action Revived;

    private PlayerState state;
    private PlayerCameraController cam;
    private CharacterController cc;
    private float graceT;

    private void Awake()
    {
        state = GetComponent<PlayerState>();
        cam = GetComponent<PlayerCameraController>();
        cc = GetComponent<CharacterController>();
        Current = maxHealth;
    }

    /// <summary>Character creation: set the pool size, refill, ping the HUD.</summary>
    public void ConfigureMax(float value)
    {
        maxHealth = Mathf.Max(1f, value);
        Current = maxHealth;
        Damaged?.Invoke(Current, maxHealth);
    }

    public void TakeDamage(float amount, Vector3 hitFrom)
    {
        if (IsDead || amount <= 0f || Invulnerable) return;
        if (state != null && state.IsInvulnerable) return; // dodge i-frames eat the hit

        Current = Mathf.Max(0f, Current - amount);
        graceT = graceSeconds;
        cam?.Shake(Current <= 0f ? deathShake : hitShake);
        Damaged?.Invoke(Current, maxHealth);
        if (Current <= 0f) Die();
    }

    private void Die()
    {
        IsDead = true;
        Died?.Invoke();
        GameLoop.Ensure().OnPlayerDied(this);
    }

    /// <summary>Checkpoint rest: refill without moving. Death goes through Revive.</summary>
    public void Heal()
    {
        Current = maxHealth;
        IsDead = false;
        Damaged?.Invoke(Current, maxHealth);
    }

    /// <summary>Partial heal — flask sips tick this.</summary>
    public void Heal(float amount)
    {
        if (IsDead || amount <= 0f) return;
        Current = Mathf.Min(maxHealth, Current + amount);
        Damaged?.Invoke(Current, maxHealth);
    }

    /// <summary>GameLoop's respawn: move to the checkpoint, refill, brief grace,
    /// and unlock input. The caller owns the death overlay + enemy reset.</summary>
    public void Revive(Vector3 pos, Quaternion rot)
    {
        if (cc != null) cc.enabled = false;
        transform.SetPositionAndRotation(pos, rot);
        if (cc != null) cc.enabled = true;
        Current = maxHealth;
        IsDead = false;
        graceT = graceSeconds * 2f;
        Damaged?.Invoke(Current, maxHealth); // bar refills visibly
        Revived?.Invoke();
    }

    private void Update()
    {
        if (graceT > 0f) graceT -= Time.deltaTime;
    }
}
