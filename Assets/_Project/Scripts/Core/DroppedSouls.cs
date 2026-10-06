using UnityEngine;

/// <summary>
/// The green-soul pile left where the player died. Runtime-built pickup:
/// small emissive sphere + trigger, bobbing in place; walking into it pays
/// the souls back into the wallet. Only one exists at a time — dying again
/// before retrieving it destroys the older pile (the souls rule).
/// </summary>
public sealed class DroppedSouls : MonoBehaviour
{
    public static DroppedSouls Current { get; private set; }

    private int amount;
    private Vector3 basePos;
    private float bobT;

    /// <summary>Drops <paramref name="souls"/> at <paramref name="pos"/>. A previous
    /// unclaimed pile is destroyed — second death forfeits it.</summary>
    public static void Drop(Vector3 pos, int souls)
    {
        if (Current != null) Destroy(Current.gameObject);
        if (souls <= 0) { Current = null; return; }

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "DroppedSouls";
        go.transform.position = pos + Vector3.up * 0.45f;
        go.transform.localScale = Vector3.one * 0.34f;
        var col = go.GetComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = 3.6f; // generous pickup radius in local (scaled) space ≈ 1.2m
        var rb = go.AddComponent<Rigidbody>(); // triggers need a rigidbody to fire OnTriggerEnter reliably
        rb.isKinematic = true;
        var rend = go.GetComponent<Renderer>();
        rend.material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = new Color(0.2f, 1f, 0.45f),
        };
        rend.material.EnableKeyword("_EMISSION");
        rend.material.SetColor("_EmissionColor", new Color(0.2f, 1f, 0.45f) * 2.2f);

        var d = go.AddComponent<DroppedSouls>();
        d.amount = souls;
        d.basePos = go.transform.position;
        Current = d;
    }

    private void Update()
    {
        bobT += Time.deltaTime;
        transform.position = basePos + Vector3.up * (Mathf.Sin(bobT * 2.2f) * 0.08f);
        transform.Rotate(0f, 60f * Time.deltaTime, 0f);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<PlayerHealth>() == null) return;
        SoulsWallet.Add(amount);
        GameHud.Toast("SOULS RETRIEVED");
        Current = null;
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (Current == this) Current = null;
    }
}
