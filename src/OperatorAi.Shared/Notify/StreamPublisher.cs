using System.Text;
using System.Threading.Channels;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Shared.Notify;

/// <summary>Serial, bounded draft delivery: first content immediately, then <=4096 chars or 100ms; SQL stores only canonical.</summary>
public sealed class StreamPublisher : IAsyncDisposable
{
    private const int BatchChars = 4096;
    private readonly NotifyClient _notify;
    private readonly StreamEvent _identity;
    private readonly Channel<string> _chunks = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop;
    private readonly Task _sending;
    private volatile bool _disabled;
    private int _sequence, _offset;

    public Guid StreamId => _identity.StreamId;
    public CancellationToken Token => _stop.Token;
    public bool LostOwnership { get; private set; }

    public StreamPublisher(NotifyClient notify, Guid conversationId, long queueItemId, string runId, int round, CancellationToken ct)
    {
        _notify = notify;
        _identity = new(conversationId, queueItemId, runId, Guid.NewGuid(), round, "started", 0, 0, null);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sending = PumpAsync();
    }

    public async Task WriteAsync(string text, CancellationToken ct)
    {
        if (_disabled) return;
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(BatchChars, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            await _chunks.Writer.WriteAsync(text.Substring(offset, count), ct);
            offset += count;
        }
    }

    public async Task FinishAsync()
    {
        _chunks.Writer.TryComplete();
        await _sending;
    }

    public async Task AbortAsync()
    {
        _chunks.Writer.TryComplete();
        try { await _sending; }
        catch (OperationCanceledException) { }
        catch (LeaseLostException) { }
        if (!_disabled && !LostOwnership)
        {
            try { await _notify.StreamAsync(_identity with { Kind = "aborted", Sequence = ++_sequence, Offset = _offset }, CancellationToken.None); }
            catch (LeaseLostException) { LostOwnership = true; }
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            _disabled = !await _notify.StreamAsync(_identity, _stop.Token);
            var buffer = new StringBuilder(BatchChars);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            var tick = timer.WaitForNextTickAsync(_stop.Token).AsTask();
            var readable = _chunks.Reader.WaitToReadAsync(_stop.Token).AsTask();
            while (true)
            {
                await Task.WhenAny(readable, tick);
                if (readable.IsCompleted)
                {
                    if (!await readable) break;
                    while (_chunks.Reader.TryRead(out var text))
                    {
                        if (_disabled) continue;
                        buffer.Append(text);
                        if (_sequence == 0 || buffer.Length >= BatchChars) await FlushAsync(buffer);
                    }
                    readable = _chunks.Reader.WaitToReadAsync(_stop.Token).AsTask();
                }
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    await FlushAsync(buffer);
                    tick = timer.WaitForNextTickAsync(_stop.Token).AsTask();
                }
            }
            await FlushAsync(buffer);
        }
        catch (LeaseLostException)
        {
            LostOwnership = true;
            await _stop.CancelAsync();
            throw;
        }
    }

    private async Task FlushAsync(StringBuilder buffer)
    {
        while (buffer.Length > 0 && !_disabled)
        {
            var count = Math.Min(BatchChars, buffer.Length);
            if (count < buffer.Length && char.IsHighSurrogate(buffer[count - 1])) count--;
            var text = buffer.ToString(0, count);
            var batch = _identity with { Kind = "delta", Sequence = ++_sequence, Offset = _offset, Text = text };
            _disabled = !await _notify.StreamAsync(batch, _stop.Token);
            _offset += count;
            buffer.Remove(0, count);
        }
        if (_disabled) buffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _chunks.Writer.TryComplete();
        try { await _sending; }
        catch (OperationCanceledException) { }
        catch (LeaseLostException) { }
        _stop.Dispose();
    }
}
