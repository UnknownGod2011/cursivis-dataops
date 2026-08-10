using System.Collections.Concurrent;

namespace Cursivis.Application.Operations;

public sealed class OperationCancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operations = new();
    private bool _disposed;

    public OperationLease Begin(Guid operationId, CancellationToken parent = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation identifier is required.", nameof(operationId));
        }

        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        if (!_operations.TryAdd(operationId, source))
        {
            source.Dispose();
            throw new InvalidOperationException("An operation with the same identifier is already active.");
        }

        return new OperationLease(operationId, source.Token, Complete);
    }

    public bool Cancel(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out CancellationTokenSource? source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    public void CancelAll()
    {
        foreach (CancellationTokenSource source in _operations.Values)
        {
            source.Cancel();
        }
    }

    private void Complete(Guid operationId)
    {
        if (_operations.TryRemove(operationId, out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach ((_, CancellationTokenSource source) in _operations)
        {
            source.Cancel();
            source.Dispose();
        }

        _operations.Clear();
    }
}

public sealed class OperationLease : IDisposable
{
    private readonly Guid _operationId;
    private readonly Action<Guid> _complete;
    private int _disposed;

    internal OperationLease(Guid operationId, CancellationToken token, Action<Guid> complete)
    {
        _operationId = operationId;
        Token = token;
        _complete = complete;
    }

    public CancellationToken Token { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _complete(_operationId);
        }
    }
}
