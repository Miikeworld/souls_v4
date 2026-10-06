using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Arena brawl volume (beat-'em-up lock-in). Entering the trigger seals the
/// arena (<see cref="seals"/> — gate visuals + blockers) and wakes the first
/// wave; each wave (a child group of inactive enemies) starts when the
/// previous one is dead. The last kill reopens the seals. Death or a
/// checkpoint rest (<see cref="EnemyAI.WorldReset"/>) re-arms it: waves are
/// hidden again and the seals open, so the fight replays like any Souls
/// encounter. Wave enemies pay their normal souls/mana rewards.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public sealed class CrowdEncounter : MonoBehaviour
{
    [SerializeField] private GameObject[] waves;
    [SerializeField] private GameObject[] seals;
    [SerializeField] private string startToast = "";
    [SerializeField] private string clearBanner = "AREA CLEARED";
    [SerializeField, Min(0f)] private float waveDelay = 1.4f;

    private readonly List<Health> live = new();
    private bool running, cleared;
    private int wave = -1;
    private float nextWaveAt = -1f;

    public bool Running => running;
    public bool Cleared => cleared;

    private void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
        Rearm();
    }

    private void OnEnable() => EnemyAI.WorldReset += Rearm;
    private void OnDisable() => EnemyAI.WorldReset -= Rearm;

    private void OnTriggerEnter(Collider other)
    {
        if (running || cleared || waves == null || waves.Length == 0) return;
        if (other.GetComponentInParent<PlayerLocomotion>() == null) return;
        running = true;
        SetSeals(true);
        if (!string.IsNullOrEmpty(startToast)) GameHud.Toast(startToast);
        StartWave(0);
    }

    private void StartWave(int i)
    {
        wave = i;
        nextWaveAt = -1f;
        live.Clear();
        var group = waves[i];
        if (group == null) return;
        group.SetActive(true);
        foreach (var h in group.GetComponentsInChildren<Health>(true))
        {
            live.Add(h);
            var ai = h.GetComponent<EnemyAI>();
            if (ai == null) continue;
            if (h.IsDead) ai.Respawn();
            ai.Alert();
        }
    }

    private void Update()
    {
        if (!running) return;
        var alive = false;
        foreach (var h in live) if (h != null && !h.IsDead) { alive = true; break; }
        if (alive) return;
        if (wave + 1 < waves.Length)
        {
            if (nextWaveAt < 0f) nextWaveAt = Time.time + waveDelay;
            else if (Time.time >= nextWaveAt) StartWave(wave + 1);
            return;
        }
        running = false;
        cleared = true;
        SetSeals(false);
        if (!string.IsNullOrEmpty(clearBanner)) GameHud.Banner(clearBanner, 2f);
    }

    /// <summary>Back to the untouched state: waves hidden, seals open.</summary>
    private void Rearm()
    {
        running = false;
        cleared = false;
        wave = -1;
        nextWaveAt = -1f;
        live.Clear();
        if (waves != null)
            foreach (var w in waves)
            {
                if (w == null) continue;
                foreach (var ai in w.GetComponentsInChildren<EnemyAI>(true))
                    if (ai.gameObject.activeInHierarchy) ai.Respawn();
                w.SetActive(false);
            }
        SetSeals(false);
    }

    private void SetSeals(bool on)
    {
        if (seals == null) return;
        foreach (var s in seals) if (s != null) s.SetActive(on);
    }
}
