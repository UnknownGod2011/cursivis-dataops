using System.Collections.Concurrent;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;

namespace Cursivis.Application.Context;

public sealed class ContextInteractionSession
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private ContextExecutionInput? _input;
    private SmartResult? _latestResult;
    private string? _clipboardText;

    public ContextInteractionSession(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ContextFingerprint? Fingerprint
    {
        get
        {
            lock (_gate)
            {
                return _input?.Context.Fingerprint;
            }
        }
    }

    public void Begin(ContextSnapshot context)
        => Begin(ContextExecutionInput.FromText(context));

    public void Begin(ContextExecutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Context.IsExpired(_timeProvider.GetUtcNow()))
        {
            throw new ArgumentException("An interaction cannot begin with expired context.", nameof(input));
        }

        lock (_gate)
        {
            _input = input;
            _latestResult = null;
            _clipboardText = null;
        }
    }

    public void SetResult(
        ContextFingerprint expectedFingerprint,
        SmartResult result,
        string? clipboardText = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (_input is null || _input.Context.Fingerprint != expectedFingerprint)
            {
                throw new InvalidOperationException("The result does not belong to the active context session.");
            }

            _latestResult = result;
            _clipboardText = string.IsNullOrWhiteSpace(clipboardText)
                ? result.FinalContent
                : clipboardText.Trim();
        }
    }

    public ContextSessionAccess GetCurrent()
    {
        lock (_gate)
        {
            if (_input is null)
            {
                return new ContextSessionAccess(ContextSessionAccessStatus.Missing, null, null, null);
            }

            if (_input.Context.IsExpired(_timeProvider.GetUtcNow()))
            {
                _input = null;
                _latestResult = null;
                _clipboardText = null;
                return new ContextSessionAccess(ContextSessionAccessStatus.Expired, null, null, null);
            }

            return new ContextSessionAccess(
                ContextSessionAccessStatus.Available,
                _input,
                _latestResult,
                _clipboardText);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _input = null;
            _latestResult = null;
            _clipboardText = null;
        }
    }
}

public sealed class ResultAutoCopyCoordinator
{
    private const int MaximumRememberedOperations = 128;
    private readonly IResultClipboardService _clipboard;
    private readonly ConcurrentDictionary<OperationId, Lazy<Task<ResultClipboardWriteResult>>> _writes = new();
    private readonly ConcurrentQueue<OperationId> _operationOrder = new();

    public ResultAutoCopyCoordinator(IResultClipboardService clipboard)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    public Task<ResultClipboardWriteResult> CopyFinalOnceAsync(
        OperationId operationId,
        string finalText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalText);
        Lazy<Task<ResultClipboardWriteResult>> write = _writes.GetOrAdd(
            operationId,
            id => new Lazy<Task<ResultClipboardWriteResult>>(
                () =>
                {
                    _operationOrder.Enqueue(id);
                    TrimCompletedHistory();
                    return _clipboard.CopyTextAsync(finalText, cancellationToken);
                },
                LazyThreadSafetyMode.ExecutionAndPublication));
        return write.Value;
    }

    private void TrimCompletedHistory()
    {
        while (_operationOrder.Count > MaximumRememberedOperations &&
               _operationOrder.TryDequeue(out OperationId oldest))
        {
            if (_writes.TryGetValue(oldest, out Lazy<Task<ResultClipboardWriteResult>>? write) &&
                (!write.IsValueCreated || !write.Value.IsCompleted))
            {
                _operationOrder.Enqueue(oldest);
                break;
            }

            _writes.TryRemove(oldest, out _);
        }
    }
}
