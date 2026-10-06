using UnityEngine;

/// <summary>
/// The souls purse — a plain static counter so no scene wiring is needed.
/// Enemies pay in on Health.Died; death drops the lot as a world pickup.
/// </summary>
public static class SoulsWallet
{
    public static int Souls { get; private set; }
    /// <summary>(current) — HUD hooks this.</summary>
    public static event System.Action<int> Changed;

    public static void Add(int n)
    {
        if (n == 0) return;
        Souls = Mathf.Max(0, Souls + n);
        Changed?.Invoke(Souls);
    }

    /// <summary>Empties the purse and returns what was in it — the death drop.</summary>
    public static int DropAll()
    {
        var s = Souls;
        if (s != 0) { Souls = 0; Changed?.Invoke(0); }
        return s;
    }
}
