using System.Threading;
using System.Threading.Tasks;

namespace SqlToOracleMigrator.Core.Migration;

/// <summary>
/// Cooperative run control for Pause/Resume.
/// Pause is honored only at safe boundaries (end of batch/end of table).
/// </summary>
public sealed class RunControl
{
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private volatile bool _pauseRequested;

    public bool IsPauseRequested => _pauseRequested;

    public void RequestPause()
    {
        _pauseRequested = true;
    }

    public void Resume()
    {
        _pauseRequested = false;
        // release any waiters
        try { _pauseGate.Release(); } catch { /* already released */ }
    }

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        if (!_pauseRequested) return;

        // Gate ensures only one waiter blocks; Release() wakes it.
        await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
        // After being released, immediately allow others through.
        _pauseGate.Release();
    }
}
