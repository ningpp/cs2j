using System.Threading;

namespace SampleConcurrencyLib;

/// <summary>
/// C29 volatile fixture (design §7.2): a spinning worker whose stop flag is a
/// primitive volatile field — the CN-7 audit counts the field and the direct
/// mapping stays untouched; the worker must observe the stop without any lock.
/// </summary>
public sealed class VolatileStopFlag
{
    private volatile bool _stopped;
    private long _iterations;

    public bool Stopped => _stopped;

    public long Iterations => Interlocked.Read(ref _iterations);

    /// <summary>Spins until the volatile stop flag is observed.</summary>
    public long RunUntilStopped()
    {
        while (!_stopped)
        {
            _iterations++;
            if ((_iterations & 0xFF) == 0)
            {
                Thread.Sleep(1);
            }
        }

        return _iterations;
    }

    public void Stop()
    {
        _stopped = true;
    }
}
