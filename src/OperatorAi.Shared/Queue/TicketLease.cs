namespace OperatorAi.Shared.Queue;

public sealed class LeaseLostException(Exception? inner = null)
    : InvalidOperationException("Kepemilikan pekerjaan hilang atau heartbeat gagal.", inner) { }

public sealed class TicketLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _heartbeat;
    public CancellationToken Token => _stop.Token;

    internal TicketLease(Func<CancellationToken, Task<bool>> heartbeat, CancellationToken owner)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(owner);
        _heartbeat = RunAsync(heartbeat);
    }

    private async Task RunAsync(Func<CancellationToken, Task<bool>> heartbeat)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                if (!await heartbeat(_stop.Token)) { _stop.Cancel(); return; }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { _stop.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _heartbeat;
        _stop.Dispose();
    }
}
