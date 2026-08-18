using System.Diagnostics;

namespace Voidforge.SoakTests;

// A soft wall-clock budget shared by the concurrent user scripts. <see cref="Reached"/> lets a script
// stop STARTING new legs once the window elapses; <see cref="PauseAsync"/> spaces successive legs by
// LegPause so their actions spread across the run. A leg already in flight is allowed to finish.
public sealed class Deadline
{
    private readonly Stopwatch _stopwatch;
    private readonly TimeSpan _window;

    public Deadline(Stopwatch stopwatch, TimeSpan window)
    {
        _stopwatch = stopwatch;
        _window = window;
    }

    public bool Reached => _stopwatch.Elapsed >= _window;

    public TimeSpan Remaining
    {
        get
        {
            var remaining = _window - _stopwatch.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public async Task PauseAsync()
    {
        var pause = SoakTimeouts.LegPause;
        await Task.Delay(pause < Remaining ? pause : Remaining);
    }
}
