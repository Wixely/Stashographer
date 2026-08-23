namespace Stashographer.Services.Intake;

/// <summary>Wakes the queue worker without making capture requests wait for processing.</summary>
public sealed class IntakeQueueSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Pulse()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another enqueue won the race; one wake-up is enough to drain the queue.
        }
    }

    public async Task WaitAsync(TimeSpan maximumDelay, CancellationToken ct)
    {
        await _signal.WaitAsync(maximumDelay, ct);
    }
}
