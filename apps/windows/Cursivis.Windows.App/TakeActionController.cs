using Cursivis.Application.Actions;
using Cursivis.Application.Context;
using Cursivis.Application.Presentation;
using Cursivis.Contracts.Browser;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Actions;
using Cursivis.Domain.Models;
using Cursivis.Infrastructure.BrowserBridge;
using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.Windows.App;

public sealed class TakeActionController : IDisposable
{
    private static readonly TimeSpan ContextLifetime = TimeSpan.FromMinutes(5);
    private readonly BrowserBridgeService _bridge;
    private readonly BrowserTakeActionService _takeAction;
    private readonly ISelectionCaptureService _selectionCapture;
    private readonly IHotkeyInputSettler _inputSettler;
    private readonly ContextTriggerController _contextTrigger;
    private readonly ContextOrbWindow _orb;
    private readonly ContextResultWindow _resultWindow;
    private readonly ModelIdentifier _model;
    private CancellationTokenSource? _operation;
    private BrowserActionProposal? _lastUndoProposal;
    private bool _disposed;

    public TakeActionController(
        BrowserBridgeService bridge,
        BrowserTakeActionService takeAction,
        ISelectionCaptureService selectionCapture,
        IHotkeyInputSettler inputSettler,
        ContextTriggerController contextTrigger,
        ContextOrbWindow orb,
        ContextResultWindow resultWindow,
        ModelIdentifier model)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _takeAction = takeAction ?? throw new ArgumentNullException(nameof(takeAction));
        _selectionCapture = selectionCapture ?? throw new ArgumentNullException(nameof(selectionCapture));
        _inputSettler = inputSettler ?? throw new ArgumentNullException(nameof(inputSettler));
        _contextTrigger = contextTrigger ?? throw new ArgumentNullException(nameof(contextTrigger));
        _orb = orb ?? throw new ArgumentNullException(nameof(orb));
        _resultWindow = resultWindow ?? throw new ArgumentNullException(nameof(resultWindow));
        _model = model;
        _contextTrigger.BrowserTakeActionRequested += OnResultTakeActionRequested;
        _contextTrigger.ExternalUndoRequested += OnExternalUndoRequested;
    }

    public void InvokeDirect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StartOperation(RunDirectAsync);
    }

    public async Task<string> ExecuteFromLiveAsync(
        string instruction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        await RunWithInstructionAsync(
            instruction,
            ActionProposalSource.Realtime,
            isDirectUserRequest: true,
            cancellationToken);
        return "The shared Take Action workflow finished. Any required confirmation and the verified outcome were shown in Cursivis.";
    }

    public void Cancel() => _operation?.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
        _contextTrigger.BrowserTakeActionRequested -= OnResultTakeActionRequested;
        _contextTrigger.ExternalUndoRequested -= OnExternalUndoRequested;
    }

    private void OnResultTakeActionRequested(string instruction) =>
        StartOperation(token => RunWithInstructionAsync(
            instruction,
            ActionProposalSource.SmartMode,
            isDirectUserRequest: false,
            token));

    private void StartOperation(Func<CancellationToken, Task> operation)
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        _ = RunSafelyAsync(operation, _operation.Token);
    }

    private async Task RunSafelyAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _orb.Hide();
        }
        catch (BrowserBridgeException exception)
        {
            _orb.Hide();
            _resultWindow.ShowFailure("Browser integration unavailable", exception.Message);
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowFailure(
                "Take Action could not continue",
                "The action stopped safely before an unverified step could continue.");
        }
    }

    private async Task RunDirectAsync(CancellationToken cancellationToken)
    {
        _contextTrigger.DismissTransientResult();
        _contextTrigger.SetExternalUndoAvailable(false);
        _lastUndoProposal = null;
        _orb.ShowState(
            OrbPresentationState.Thinking,
            "Reading the request",
            "Checking the selected text and active browser tab");
        await _inputSettler.WaitForModifiersReleasedAsync(cancellationToken);

        SelectionCaptureResult capture = await _selectionCapture.CaptureAsync(
            ContextLifetime,
            cancellationToken);
        string? instruction = capture.Status == SelectionCaptureStatus.Captured
            ? capture.Context?.Text
            : null;
        if (string.IsNullOrWhiteSpace(instruction) && _bridge.Snapshot.IsConnected)
        {
            try
            {
                BrowserSelectionResponse browserSelection = await _bridge.GetSelectionAsync(
                    includeNearbySemanticContext: true,
                    cancellationToken: cancellationToken);
                instruction = browserSelection.SelectedText;
            }
            catch (BrowserBridgeException exception) when (
                exception.Code is "target_missing" or "browser_request_timeout")
            {
                // A missing selection still permits an explicit typed request.
            }
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            var input = new QuickTaskInputWindow(
                "Take Action",
                _resultWindow.CurrentTheme,
                "What should Cursivis do in the visible form?");
            instruction = await input.ShowAsync(cancellationToken, _orb.OverlayBounds);
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            _orb.Hide();
            return;
        }

        await RunWithInstructionAsync(
            instruction,
            ActionProposalSource.DirectUserRequest,
            isDirectUserRequest: true,
            cancellationToken);
    }

    private async Task RunWithInstructionAsync(
        string instruction,
        ActionProposalSource source,
        bool isDirectUserRequest,
        CancellationToken cancellationToken)
    {
        if (!_bridge.Snapshot.IsConnected)
        {
            throw new BrowserBridgeException(
                "bridge_disconnected",
                "Connect the Cursivis browser extension from Browser Integration in Settings.",
                retryable: true);
        }

        _resultWindow.Hide();
        _contextTrigger.SetExternalUndoAvailable(false);
        _lastUndoProposal = null;
        string effectiveInstruction = instruction.Trim();
        BrowserFormSnapshot? form = null;
        for (int planningPass = 0; planningPass < 4; planningPass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _orb.ShowState(
                OrbPresentationState.Thinking,
                form is null ? "Inspecting the visible form" : "Updating the plan",
                "Only visible, non-sensitive controls are eligible");
            form ??= await _bridge.DiscoverFormAsync(cancellationToken);
            BrowserActionPlanningResult planning = await _takeAction.PlanAsync(
                form,
                effectiveInstruction,
                source,
                isDirectUserRequest,
                _model,
                cancellationToken);
            if (planning.Status == BrowserActionPlanningStatus.Cancelled)
            {
                _orb.Hide();
                return;
            }

            if (planning.Status == BrowserActionPlanningStatus.NeedsClarification)
            {
                var clarification = new QuickTaskInputWindow(
                    "Take Action",
                    _resultWindow.CurrentTheme,
                    planning.ClarifyingQuestion);
                string? answer = await clarification.ShowAsync(cancellationToken, _orb.OverlayBounds);
                if (string.IsNullOrWhiteSpace(answer))
                {
                    _orb.Hide();
                    return;
                }

                effectiveInstruction += $"\n\nClarification answer: {answer.Trim()}";
                continue;
            }

            if (planning.Status != BrowserActionPlanningStatus.Ready || planning.Proposal is null)
            {
                OpenAiFailure? failure = planning.Failure;
                _orb.Hide();
                _resultWindow.ShowFailure(
                    "Take Action plan unavailable",
                    failure?.SafeMessage ?? "OpenAI could not create a valid plan for the visible form.");
                return;
            }

            BrowserActionProposal proposal = planning.Proposal;
            BrowserActionExecutionResult execution = await _takeAction.ExecuteAsync(
                proposal,
                cancellationToken: cancellationToken);
            if (execution.Status == BrowserActionExecutionStatus.ConfirmationRequired)
            {
                var review = new ActionPlanReviewWindow(
                    proposal,
                    execution.Policy,
                    _resultWindow.CurrentTheme);
                ActionPlanReviewResult choice = await review.ShowAsync(
                    _orb.OverlayBounds,
                    cancellationToken);
                if (!choice.Confirmed)
                {
                    _orb.ShowState(
                        OrbPresentationState.Cancelled,
                        "Action cancelled",
                        "No remaining steps will run");
                    await Task.Delay(500, cancellationToken);
                    _orb.Hide();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(choice.AdditionalInstruction))
                {
                    effectiveInstruction += $"\n\nAdditional instruction: {choice.AdditionalInstruction}";
                    continue;
                }

                ActionConfirmation confirmation = _takeAction.CreateConfirmation(proposal);
                _orb.ShowState(
                    OrbPresentationState.Executing,
                    "Running verified steps",
                    "Cursivis will stop if the page changes");
                execution = await _takeAction.ExecuteAsync(
                    proposal,
                    confirmation,
                    cancellationToken);
            }
            else
            {
                _orb.ShowState(
                    OrbPresentationState.Executing,
                    "Running verified steps",
                    "Cursivis will stop if the page changes");
            }

            PresentExecution(proposal, execution);
            return;
        }

        _orb.Hide();
        _resultWindow.ShowFailure(
            "Take Action needs a clearer request",
            "The request changed several times without producing a stable executable plan.");
    }

    private void PresentExecution(
        BrowserActionProposal proposal,
        BrowserActionExecutionResult execution)
    {
        if (execution.Status == BrowserActionExecutionStatus.Completed && execution.Receipt is not null)
        {
            bool canUndo = !proposal.UndoCommands.IsDefaultOrEmpty;
            _lastUndoProposal = canUndo ? proposal : null;
            _contextTrigger.SetExternalUndoAvailable(canUndo);
            _orb.ShowState(
                OrbPresentationState.Done,
                "Action complete",
                "Every completed step was verified");
            _resultWindow.ShowActionOutcome(
                $"Completed and verified {execution.Receipt.Steps.Length} browser {(execution.Receipt.Steps.Length == 1 ? "step" : "steps")}.",
                canUndo);
            _orb.Hide();
            return;
        }

        _orb.Hide();
        _contextTrigger.SetExternalUndoAvailable(false);
        _resultWindow.ShowFailure(
            "Take Action stopped",
            execution.BrowserFailure?.SafeMessage ??
            execution.Policy.Reasons.FirstOrDefault()?.Message ??
            "The action was not verified, so no remaining steps were run.");
    }

    private async void OnExternalUndoRequested(object? sender, EventArgs args)
    {
        BrowserActionProposal? proposal = _lastUndoProposal;
        if (proposal is null)
        {
            _contextTrigger.SetExternalUndoAvailable(false);
            return;
        }

        try
        {
            _resultWindow.Hide();
            _orb.ShowState(
                OrbPresentationState.Executing,
                "Undoing browser changes",
                "Restoring the previous verified field values");
            ExecutionReceipt receipt = await _takeAction.UndoAsync(proposal);
            if (receipt.State == ActionOutcomeState.RolledBack)
            {
                _lastUndoProposal = null;
                _contextTrigger.SetExternalUndoAvailable(false);
                _orb.ShowState(
                    OrbPresentationState.Done,
                    "Changes restored",
                    "The previous field values were verified");
                _resultWindow.ShowActionOutcome("The browser field changes were undone and verified.", false);
                _orb.Hide();
                return;
            }

            _orb.Hide();
            _resultWindow.ShowFailure(
                "Undo stopped",
                "The page changed before all previous field values could be restored.");
        }
        catch (Exception)
        {
            _orb.Hide();
            _contextTrigger.SetExternalUndoAvailable(false);
            _resultWindow.ShowFailure(
                "Undo unavailable",
                "The original browser form is no longer active or its context expired.");
        }
    }
}
