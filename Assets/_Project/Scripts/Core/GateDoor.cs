using UnityEngine;

/// <summary>
/// A sliding gate (portcullis, slab). Closed by default; Open() lifts it
/// openLift metres over openTime. The blocker collider rides the visual so
/// "open" really means passable — no separate enabled flag to desync.
/// </summary>
public sealed class GateDoor : MonoBehaviour
{
    [Tooltip("Transform that lifts — the portcullis mesh + its blocker.")]
    [SerializeField] private Transform door;
    [SerializeField, Min(0.5f)] private float openLift = 4f;
    [SerializeField, Min(0.1f)] private float openTime = 1.4f;
    [SerializeField] private bool startOpen;

    public bool IsOpen { get; private set; }

    private float closedY, openY, t;

    private void Start()
    {
        if (door == null) door = transform;
        closedY = door.localPosition.y;
        openY = closedY + openLift;
        IsOpen = startOpen;
        t = IsOpen ? 1f : 0f;
        door.localPosition = new Vector3(door.localPosition.x,
            Mathf.Lerp(closedY, openY, t), door.localPosition.z);
    }

    /// <summary>Lever/plate call — latched: once open it stays open (Souls shortcut rule).</summary>
    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        GameHud.Toast("A GATE GROANS OPEN");
    }

    private void Update()
    {
        var want = IsOpen ? 1f : 0f;
        if (Mathf.Approximately(t, want)) return;
        t = Mathf.MoveTowards(t, want, Time.deltaTime / openTime);
        // Smoothstep — heavy gate eases out then slams home.
        var k = t * t * (3f - 2f * t);
        door.localPosition = new Vector3(door.localPosition.x,
            Mathf.Lerp(closedY, openY, k), door.localPosition.z);
    }
}
