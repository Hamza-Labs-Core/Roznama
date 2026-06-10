using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Calendar.Application.Calendars;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Singleton in-process change feed (API.md <c>GET /sync/stream</c>). Each listener gets a bounded
/// drop-oldest channel so publishing is always non-blocking and a stalled SSE connection can't grow memory;
/// the UI semantics are "something changed → reload", so dropped intermediate events lose nothing.
/// </summary>
public sealed class ChangeFeed : IChangeFeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int BufferPerSubscriber = 64;

    private readonly ConcurrentDictionary<Guid, Channel<ChangeEvent>> _subscribers = new();

    public void Publish(string type, object? payload = null)
    {
        var ev = new ChangeEvent(type, payload is null ? "{}" : JsonSerializer.Serialize(payload, Json));
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(ev);
    }

    public async IAsyncEnumerable<ChangeEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ChangeEvent>(new BoundedChannelOptions(BufferPerSubscriber)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var ev))
                    yield return ev;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
