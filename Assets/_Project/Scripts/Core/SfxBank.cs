using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One-stop audio hook: WeaponArt sfxCues (and any gameplay code) call
/// SfxBank.Play(id, pos). Clips are wired on the bank's `entries` list by the
/// bank, with project-owned CC0 clips in Resources as defaults. Unknown ids
/// remain silent (warned once). Self-creates a host with a small
/// AudioSource pool the first time something plays.
/// </summary>
public sealed class SfxBank : MonoBehaviour
{
    [System.Serializable]
    public sealed class Entry
    {
        public string id;
        public AudioClip clip;
        [Range(0f, 1f)] public float volume = 1f;
    }

    [SerializeField] private Entry[] entries;
    [SerializeField, Min(2)] private int poolSize = 6;

    private static SfxBank inst;
    private static readonly HashSet<string> warned = new();
    private AudioSource[] pool;
    private int next;
    private readonly Dictionary<string, AudioClip> resourceClips = new();

    /// <summary>The live bank — creates one on demand (no scene wiring).</summary>
    public static SfxBank Ensure()
    {
        if (inst != null) return inst;
        inst = FindFirstObjectByType<SfxBank>();
        if (inst != null) return inst;
        var go = new GameObject("SfxBank");
        inst = go.AddComponent<SfxBank>();
        return inst;
    }

    /// <summary>Play a named clip at a world position. Silent no-op when the
    /// id isn't wired — audio arrives as data, not code changes.</summary>
    public static void Play(string id, Vector3 pos, float volume = 1f)
    {
        if (string.IsNullOrEmpty(id)) return;
        var bank = Ensure();
        var clip = bank.Lookup(id, out var vol);
        if (clip == null)
        {
            if (warned.Add(id))
                Debug.Log($"[SfxBank] '{id}' has no clip wired — silent (hook ready).");
            return;
        }
        var src = bank.pool[bank.NextIndex()];
        src.transform.position = pos;
        src.pitch = Random.Range(0.94f, 1.06f);
        src.PlayOneShot(clip, vol * volume);
    }

    private AudioClip Lookup(string id, out float volume)
    {
        volume = 1f;
        if (entries != null) foreach (var e in entries)
            if (e != null && e.id == id && e.clip != null) { volume = e.volume; return e.clip; }
        // Authored banks retain precedence. Small CC0 defaults work in every scene.
        string name = id switch {
            "art.slash" => "Slash",
            "art.spin" or "art.throw" or "art.rush" or "art.teleport" => "Swish",
            "art.slam" or "art.burst" => "Slam",
            "art.palm" or "weapon.hit" => "Hit",
            "weapon.draw" => "Draw",
            "move.jump" => "Jump",
            "move.land" => "Land",
            _ => null
        };
        if(name==null)return null;
        volume = id is "art.slam" or "art.burst" ? 0.4f : 0.25f;
        if(!resourceClips.TryGetValue(name,out var clip)) resourceClips[name]=clip=Resources.Load<AudioClip>("Audio/Sfx/"+name);
        return clip;
    }

    private int NextIndex()
    {
        var i = next;
        next = (next + 1) % pool.Length;
        return i;
    }

    private void Awake()
    {
        if (inst != null && inst != this) { Destroy(gameObject); return; }
        inst = this;
        pool = new AudioSource[poolSize];
        for (var i = 0; i < pool.Length; i++)
        {
            var child = new GameObject("src" + i);
            child.transform.SetParent(transform, false);
            var src = child.AddComponent<AudioSource>();
            src.spatialBlend = 1f;    // 3D positional
            src.maxDistance = 30f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.playOnAwake = false;
            pool[i] = src;
        }
    }
}
