/// <summary>One latest request, with a fixed lifetime. Waiting for recovery does not consume it.</summary>
public sealed class RecoveryBuffer<T>
{
    private T value;
    private float until;
    public bool HasRequest { get; private set; }
    public void Set(T request, float now) { value = request; until = now + 0.25f; HasRequest = true; }
    public void Clear() { value = default; HasRequest = false; }
    public bool TryTake(float now, bool recovered, out T request)
    {
        request = default;
        if (!HasRequest) return false;
        if (now > until) { Clear(); return false; }
        if (!recovered) return false;
        request = value; Clear(); return true;
    }
}
public static class CombatContactClock
{
    public static bool Crosses(float previous, float current, float start, float end)
        => current >= previous && previous <= end && current >= start;
}
