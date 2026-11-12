namespace Volna.Core;

/// <summary>The part of a video being chosen, in whole seconds, shared by everything in the window that shows it.</summary>
public sealed class ClipRange
{
    public const double Shortest = 1;

    public double Duration { get; private set; }
    public double Start { get; private set; }
    public double End { get; private set; }
    public double Length => End - Start;

    public event Action? Changed;

    public void Reset(double duration, double start, double end)
    {
        Duration = Math.Max(Shortest, Math.Floor(duration));
        Start = End = -1;
        Set(start, end);
    }

    public void Set(double start, double end)
    {
        start = Math.Clamp(Math.Round(start), 0, Duration - Shortest);
        end = Math.Clamp(Math.Round(end), start + Shortest, Duration);
        if (start == Start && end == End) return;
        Start = start;
        End = end;
        Changed?.Invoke();
    }

    /// <summary>Moves the part, keeping its length.</summary>
    public void MoveTo(double start)
    {
        double length = Length;
        start = Math.Clamp(Math.Round(start), 0, Duration - length);
        Set(start, start + length);
    }
}
