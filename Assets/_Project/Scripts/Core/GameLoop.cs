using System.Collections;
using UnityEngine;

/// <summary>
/// The souls loop in one place: player death ??death anim ??YOU DIED ??fade ??/// souls dropped where you fell ??respawn at the last checkpoint ??every enemy
/// teleported home and healed ??fade back in. Also owns the respawn point and
/// the checkpoint rest flow (sit ??refill/reset ??menu ??stand, plus travel).
/// Self-creates (plus a lit checkpoint near spawn) so nothing needs wiring.
/// </summary>
public sealed class GameLoop : MonoBehaviour
{
    private static GameLoop instance;

    public static GameLoop Ensure()
    {
        if (instance != null) return instance;
        instance = FindFirstObjectByType<GameLoop>();
        if (instance == null)
            instance = new GameObject("GameLoop").AddComponent<GameLoop>();
        return instance;
    }

    private PlayerHealth playerHealth;
    private PlayerState state;
    private PlayerStamina stamina;
    private PlayerMana mana;
    private EstusFlask flask;
    private Animator animator;
    private Inventory inv;
    private WeaponSocket socket;

    private Vector3 checkpointPos;
    private Quaternion checkpointRot;
    private bool checkpointSet;
    private bool sequenceRunning;

    private static readonly int DeathStateId = Animator.StringToHash("Base Layer.Death");
    private static readonly int LocomotionId = Animator.StringToHash("Base Layer.Locomotion");
    private static readonly int SitId = Animator.StringToHash("Base Layer.CheckpointSit");
    private static readonly int SitIdleId = Animator.StringToHash("Base Layer.CheckpointIdle");
    private static readonly int StandId = Animator.StringToHash("Base Layer.CheckpointStand");

    /// <summary>True from sitting down until standing back up.</summary>
    public static bool IsResting { get; private set; }

    private CharacterController cc;
    private Checkpoint restingAt;
    private Coroutine restRoutine;

    /// <summary>CrossFade back to locomotion ??guarded for a missing state.</summary>
    public static void PlayLocomotion(Animator a)
    {
        if (a != null && a.HasState(0, LocomotionId)) a.CrossFadeInFixedTime(LocomotionId, 0.2f, 0);
    }

    private void Start()
    {
        FindPlayer();
        if (playerHealth != null)
        {
            checkpointPos = playerHealth.transform.position;
            checkpointRot = playerHealth.transform.rotation;
            // Souls systems live on the player ??AddComponent means zero scene wiring.
            flask = playerHealth.GetComponent<EstusFlask>();
            if (flask == null) flask = playerHealth.gameObject.AddComponent<EstusFlask>();
            if (playerHealth.GetComponent<WeaponArtCaster>() == null)
                playerHealth.gameObject.AddComponent<WeaponArtCaster>();
            // The satchel + persisted souls — katana is always owned.
            inv = playerHealth.GetComponent<Inventory>();
            if (inv == null) inv = playerHealth.gameObject.AddComponent<Inventory>();
            socket = playerHealth.GetComponent<WeaponSocket>();
            SaveGame.Load(inv);
            // A lit checkpoint near spawn if the scene has none; otherwise make
            // sure at least one is lit (nearest to spawn) so death has a home.
            Checkpoint.EnsureNear(checkpointPos + Vector3.right * 3.5f);
            Checkpoint nearest = null, lit = null;
            foreach (var c in Checkpoint.All)
            {
                if (c.Lit && lit == null) lit = c;
                if (nearest == null || (c.transform.position - checkpointPos).sqrMagnitude
                                       < (nearest.transform.position - checkpointPos).sqrMagnitude) nearest = c;
            }
            if (lit == null && nearest != null) nearest.LightSilently();
            GameHud.Ensure();
            GameHud.SetFlask(flask.Charges, flask.MaxCharges);
        }
    }

    private void FindPlayer()
    {
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco == null) return;
        playerHealth = loco.GetComponent<PlayerHealth>();
        state = loco.GetComponent<PlayerState>();
        stamina = loco.GetComponent<PlayerStamina>();
        mana = loco.GetComponent<PlayerMana>();
        inv = loco.GetComponent<Inventory>();
        socket = loco.GetComponent<WeaponSocket>();
        cc = loco.GetComponent<CharacterController>();
        foreach (var a in loco.GetComponentsInChildren<Animator>(true))
            if (a != null && a.enabled && a.runtimeAnimatorController != null && a.avatar != null && a.avatar.isHuman)
            { animator = a; break; }
        // First find defaults the checkpoint to the player's spawn.
        if (!checkpointSet && playerHealth != null)
            SetCheckpoint(playerHealth.transform.position, playerHealth.transform.rotation);
    }

    /// <summary>Bonfires (and anything else) move the respawn point.</summary>
    public void SetCheckpoint(Vector3 pos, Quaternion rot)
    {
        checkpointPos = pos;
        checkpointRot = rot;
        checkpointSet = true;
    }

    // ---------- death ----------

    public void OnPlayerDied(PlayerHealth dead)
    {
        if (sequenceRunning) return;
        FindPlayer(); // a death before Start() ran would leave refs null
        StartCoroutine(DeathSequence(dead));
    }

    private IEnumerator DeathSequence(PlayerHealth dead)
    {
        sequenceRunning = true;
        if (playerHealth == null) FindPlayer();
        var deathPos = dead != null ? dead.transform.position : transform.position;
        // Killed mid-rest (menu open, sitting): tear the rest flow down.
        if (restRoutine != null) { StopCoroutine(restRoutine); restRoutine = null; }
        CheckpointMenu.Close();
        InventoryMenu.Close();
        ShopMenu.Close();
        IsResting = false;
        restingAt = null;

        // Lock everything out ??movement, attacks, dodges, slides, lock-on
        // all early-out on IsDead; flags cleared so nothing stays latched.
        if (state != null)
        {
            state.IsDead = true;
            state.IsDisplacing = false;
            state.IsRooted = false;
            state.IsInvulnerable = false;
        }
        Time.timeScale = 1f; // a mid-attack hitstop must not slow the death screen

        if (animator != null && animator.HasState(0, DeathStateId))
            animator.CrossFadeInFixedTime(DeathStateId, 0.15f, 0);

        yield return new WaitForSeconds(0.9f);                 // the body falls
        yield return GameHud.DeathOverlay(1.6f);               // YOU DIED slam
        yield return GameHud.FadeScreen(1f, 0.5f);             // to black

        // Drop what you were carrying where you fell ??second death overwrites it.
        var dropped = SoulsWallet.DropAll();
        if (dropped > 0) DroppedSouls.Drop(deathPos, dropped);

        dead.Revive(checkpointPos, checkpointRot);
        // Death is CrossFade-entered with no exit transitions ??kick the
        // animator back to locomotion or the corpse pose persists.
        if (animator != null && animator.HasState(0, LocomotionId))
            animator.CrossFadeInFixedTime(LocomotionId, 0.2f, 0);
        stamina?.Refill();
        mana?.Refill();
        flask?.Refill();
        EnemyAI.RespawnAll();
        BossGolem.ResetAll();
        BossLord.ResetAll();
        SaveNow(); // the emptied wallet persists — the drop waits in the world

        if (state != null) state.IsDead = false;
        yield return GameHud.FadeDeathOut(0.3f);
        yield return GameHud.FadeScreen(0f, 0.7f);
        sequenceRunning = false;
    }

    private void OnDestroy()
    {
        // Statics outlive the scene ??leaving to the menu mid-rest must not
        // strand the next session "resting".
        IsResting = false;
        if (instance == this) instance = null;
    }

    // ---------- checkpoint rest ----------

    /// <summary>Sit at a lit checkpoint: rest transaction, then the menu.</summary>
    public void RestAt(Checkpoint cp)
    {
        if (IsResting || sequenceRunning || cp == null) return;
        if (playerHealth == null) FindPlayer();
        restRoutine = StartCoroutine(SitRoutine(cp, fromTravel: false));
    }

    private IEnumerator SitRoutine(Checkpoint cp, bool fromTravel)
    {
        IsResting = true;
        restingAt = cp;
        if (state != null) state.IsRooted = true;
        PlaceAt(cp.RestPosition, cp.RestRotation);
        if (animator != null)
        {
            if (fromTravel && animator.HasState(0, SitIdleId)) animator.Play(SitIdleId, 0, 0f);
            else if (animator.HasState(0, SitId)) animator.CrossFadeInFixedTime(SitId, 0.2f, 0);
        }
        RestTransaction(cp);
        if (!fromTravel) yield return new WaitForSeconds(0.6f); // let the sit land before the menu slides in
        CheckpointMenu.Open(cp);
        restRoutine = null;
    }

    /// <summary>The rest trade: respawn point set, everything refilled
    /// (flasks included), every enemy back home at full health.</summary>
    public void RestTransaction(Checkpoint cp)
    {
        SetCheckpoint(cp.RestPosition, cp.RestRotation);
        playerHealth?.Heal();
        stamina?.Refill();
        mana?.Refill();
        flask?.Refill();
        EnemyAI.RespawnAll();
        BossGolem.ResetAll();
        BossLord.ResetAll();
        SaveNow(); // resting is the save ritual
        GameHud.Toast("RESTED");
    }

    /// <summary>Write the wallet + satchel + owned weapons to save.json.</summary>
    private void SaveNow()
    {
        if (inv != null) SaveGame.Save(inv, socket != null ? socket.Set : null);
    }

    private void OnApplicationQuit() => SaveNow();

    /// <summary>LEAVE: stand up, then hand control back.</summary>
    public void Leave()
    {
        if (!IsResting) return;
        CheckpointMenu.Close();
        restRoutine = StartCoroutine(StandRoutine());
    }

    private IEnumerator StandRoutine()
    {
        var len = 0f;
        if (animator != null && animator.HasState(0, StandId))
        {
            animator.CrossFadeInFixedTime(StandId, 0.15f, 0);
            yield return null;
            var next = animator.GetNextAnimatorStateInfo(0);
            len = next.fullPathHash == StandId && next.length > 0.1f ? next.length : 1.2f;
        }
        yield return new WaitForSeconds(len * 0.9f);
        PlayLocomotion(animator);
        if (state != null && !state.IsDead) state.IsRooted = false;
        IsResting = false;
        restingAt = null;
        restRoutine = null;
    }

    /// <summary>TRAVEL: fade out, warp to the destination, rest there, fade in seated.</summary>
    public void TravelTo(Checkpoint dest)
    {
        if (!IsResting || dest == null) return;
        CheckpointMenu.Close();
        restRoutine = StartCoroutine(TravelRoutine(dest));
    }

    private IEnumerator TravelRoutine(Checkpoint dest)
    {
        yield return GameHud.FadeScreen(1f, 0.4f);
        yield return SitRoutine(dest, fromTravel: true);
        yield return GameHud.FadeScreen(0f, 0.5f);
    }

    private void PlaceAt(Vector3 pos, Quaternion rot)
    {
        if (playerHealth == null) return;
        if (cc != null) cc.enabled = false;
        playerHealth.transform.SetPositionAndRotation(pos, rot);
        if (cc != null) cc.enabled = true;
    }
}

