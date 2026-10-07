using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal sealed class ScriptedLegacyByteTransport : ILegacyByteTransport
{
    private readonly object _writeGate = new();
    private readonly byte[] _inbound;
    private readonly int[] _readChunks;
    private readonly MemoryStream _written = new();
    private readonly List<byte[]> _writeChunks = [];
    private int _activeWrites;
    private int _disconnectStarted;
    private int _inboundOffset;
    private int _readChunkIndex;

    public ScriptedLegacyByteTransport(
        byte[]? inbound = null,
        int[]? readChunks = null,
        string remoteEndPoint = "fixture:1234")
    {
        _inbound = inbound ?? [];
        _readChunks = readChunks is { Length: > 0 }
            ? readChunks
            : [int.MaxValue];
        RemoteEndPoint = remoteEndPoint;
    }

    public string RemoteEndPoint { get; }

    public int DisconnectCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public int MaximumConcurrentWrites { get; private set; }

    public int WriteCount { get; private set; }

    public byte[] WrittenBytes
    {
        get
        {
            lock (_writeGate)
            {
                return _written.ToArray();
            }
        }
    }

    public IReadOnlyList<byte[]> WrittenChunks
    {
        get
        {
            lock (_writeGate)
            {
                return _writeChunks.Select(chunk => (byte[])chunk.Clone()).ToArray();
            }
        }
    }

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_inboundOffset >= _inbound.Length)
        {
            return ValueTask.FromResult(0);
        }

        var chunkLimit = Math.Max(
            1,
            _readChunks[_readChunkIndex++ % _readChunks.Length]);
        var count = Math.Min(
            destination.Length,
            Math.Min(chunkLimit, _inbound.Length - _inboundOffset));
        _inbound.AsMemory(_inboundOffset, count).CopyTo(destination);
        _inboundOffset += count;
        return ValueTask.FromResult(count);
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = source.ToArray();
        var activeWrites = Interlocked.Increment(ref _activeWrites);
        MaximumConcurrentWrites = Math.Max(
            MaximumConcurrentWrites,
            activeWrites);

        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_writeGate)
            {
                _written.Write(copy);
                _writeChunks.Add(copy);
                WriteCount++;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWrites);
        }
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnectStarted, 1) != 0)
        {
            return;
        }

        DisconnectCount++;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _written.Dispose();
        return ValueTask.CompletedTask;
    }
}
