using UnityEngine;

/// <summary>
/// Ring of skull meshes that pop outward and sink — the toxic art's signature
/// cluster. Lives on the FX_ToxicSkulls prefab root; animates the children of
/// the "Skulls" child so the poison particle child is untouched. Destruction is
/// handled by the parent prefab's ArtFx lifetime — skulls just shrink to zero.
/// ponytail: scale-out is the fade — no material alpha work on Synty Lit props.
/// </summary>
public sealed class SkullBurst : MonoBehaviour
{
    [SerializeField, Min(0.2f)] private float radius = 2.4f;
    [SerializeField] private float rise = 0.7f;
    [SerializeField] private float sink = 1.1f;
    [SerializeField] private float spin = 200f;
    [SerializeField, Min(0.3f)] private float life = 1.2f;

    private Transform[] skulls;
    private Vector3[] dirs;
    private float[] spins;
    private float age;

    private void Awake()
    {
        var holder = transform.Find("Skulls");
        if (holder == null) holder = transform;
        var list = new System.Collections.Generic.List<Transform>();
        foreach (Transform c in holder) list.Add(c);
        skulls = list.ToArray();
        dirs = new Vector3[skulls.Length];
        spins = new float[skulls.Length];
        for (var i = 0; i < skulls.Length; i++)
        {
            var a = (i + 0.35f) / skulls.Length * Mathf.PI * 2f;
            dirs[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            spins[i] = Random.Range(-1f, 1f);
            skulls[i].localScale = Vector3.zero;
            skulls[i].localPosition = dirs[i] * 0.25f;
        }
    }

    private void Update()
    {
        age += Time.deltaTime;
        var k = Mathf.Clamp01(age / life);
        var ease = 1f - (1f - k) * (1f - k);
        var pop = k < 0.12f ? k / 0.12f : k > 0.7f ? 1f - (k - 0.7f) / 0.3f : 1f;
        for (var i = 0; i < skulls.Length; i++)
        {
            if (skulls[i] == null) continue;
            skulls[i].localPosition = dirs[i] * (0.25f + radius * ease)
                + Vector3.up * (rise * Mathf.Sin(k * Mathf.PI) - sink * k * k);
            skulls[i].localScale = Vector3.one * Mathf.Max(0.01f, pop);
            skulls[i].localRotation = Quaternion.Euler(k * spin * spins[i], k * spin * 0.45f, k * spin * -spins[i]);
        }
    }
}
