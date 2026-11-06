namespace FlugelKranz.Core;

/// <summary>The last few timestamped samples of a value, for velocities measured over a fixed time.</summary>
internal sealed class MotionWindow<T> where T : struct
{
    private readonly (double Time, T Value)[] samples = new (double, T)[64];
    private int count, next;

    public void Clear() => count = next = 0;

    public void Add(double time, T value)
    {
        samples[next] = (time, value);
        next = (next + 1) % samples.Length;
        count = Math.Min(count + 1, samples.Length);
    }

    /// <summary>The newest sample at least <paramref name="age"/> seconds old, or the oldest one if none is that old yet.</summary>
    public bool TryGetReference(double now, double age, out double time, out T value)
    {
        time = 0;
        value = default;
        for (int i = 1; i <= count; i++)
        {
            (time, value) = samples[(next - i + samples.Length) % samples.Length];
            if (now - time >= age)
                break;
        }
        return count > 0;
    }
}
