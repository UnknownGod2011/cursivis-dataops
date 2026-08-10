using System.Diagnostics;
using Cursivis.Application.Context;
using Cursivis.Application.Operations;
using Cursivis.Application.Presentation;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Foreground;
using Cursivis.Windows.Platform.Hotkeys;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App;

public sealed class ContextTriggerController : IDisposable
{
    private static readonly TimeSpan ContextLifetime = TimeSpan.FromMinutes(5);
    private readonly ISelectionCaptureService _capture;
    private readonly IRegionContextCaptureService _regionCapture;
    private readonly IContextTriggerService _trigger;
    private readonly ISelectionReplacementService _replacement;
    private readonly ITextInsertionService _insertion;
    private readonly ITextUndoService _undo;
    private readonly IResultClipboardService _clipboard;
    private readonly IForegroundWindowActivator _activator;
    private readonly IForegroundWindowIdentityProvider _foreground;
    private readonly IHotkeyInputSettler _inputSettler;
    private readonly ContextOrbWindow _orb;
    private readonly ContextResultWindow _resultWindow;
    private readonly OperationCancellationRegistry _operations = new();
    private readonly ContextInteractionSession _session = new();
    private readonly ResultAutoCopyCoordinator _autoCopy;
    private readonly Stack<ContextSnapshot> _undoHistory = new();
    private readonly ModelIdentifier _model;
    private InteractionMode _defaultMode;
    private bool _closeResultsAfterInsert;
    private IReadOnlyList<GuidedOption>? _guidedOptions;
    private ContextFingerprint? _guidedOptionsFingerprint;
    private bool _externalUndoAvailable;
    private bool _disposed;

    public ContextTriggerController(
        ISelectionCaptureService capture,
        IRegionContextCaptureService regionCapture,
        IContextTriggerService trigger,
        ISelectionReplacementService replacement,
        ITextInsertionService insertion,
        ITextUndoService undo,
        IResultClipboardService clipboard,
        IForegroundWindowActivator activator,
        IForegroundWindowIdentityProvider foreground,
        IHotkeyInputSettler inputSettler,
        ContextOrbWindow orb,
        ContextResultWindow resultWindow,
        ModelIdentifier model,
        InteractionMode defaultMode = InteractionMode.Smart,
        bool closeResultsAfterInsert = false)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _regionCapture = regionCapture ?? throw new ArgumentNullException(nameof(regionCapture));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
        _insertion = insertion ?? throw new ArgumentNullException(nameof(insertion));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _autoCopy = new ResultAutoCopyCoordinator(_clipboard);
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _inputSettler = inputSettler ?? throw new ArgumentNullException(nameof(inputSettler));
        _orb = orb ?? throw new ArgumentNullException(nameof(orb));
        _resultWindow = resultWindow ?? throw new ArgumentNullException(nameof(resultWindow));
        _model = model;
        _defaultMode = defaultMode;
        _closeResultsAfterInsert = closeResultsAfterInsert;

        _resultWindow.UndoRequested += OnUndoRequested;
        _resultWindow.InsertRequested += OnInsertRequested;
        _resultWindow.TakeActionRequested += OnTakeActionRequested;
        _resultWindow.MoreOptionsRequested += OnMoreOptionsRequested;
        _resultWindow.RefineRequested += OnRefineRequested;
        _resultWindow.CloseRequested += OnCloseRequested;
        _orb.CancelRequested += OnOrbCancelRequested;
        _orb.GuidedOperationRequested += OnGuidedOperationRequested;
    }

    public TimeSpan LastOrbPresentationLatency { get; private set; }

    public TimeSpan LastCaptureLatency { get; private set; }

    public TimeSpan LastModelLatency { get; private set; }

    public event Action<string>? BrowserTakeActionRequested;

    public event EventHandler? ExternalUndoRequested;

    public void SetDefaultMode(InteractionMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        _defaultMode = mode;
    }

    public void SetCloseResultsAfterInsert(bool closeResultsAfterInsert) =>
        _closeResultsAfterInsert = closeResultsAfterInsert;

    public void Invoke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetExternalUndoAvailable(false);
        _ = RunSafelyAsync();
    }

    public void InvokeQuickTask(QuickTaskDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(definition);
        SetExternalUndoAvailable(false);
        _ = RunQuickTaskSafelyAsync(definition);
    }

    public void SetExternalUndoAvailable(bool available)
    {
        _externalUndoAvailable = available;
        _resultWindow.SetUndoAvailable(available || _undoHistory.Count > 0);
    }

    public Task<ContextTriggerExecutionResult> ExecuteQuickTaskFixtureAsync(
        ContextExecutionInput input,
        QuickTaskDefinition definition,
        CancellationToken cancellationToken = default) =>
        _trigger.ExecuteQuickTaskAsync(input, definition, _model, cancellationToken);

    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        _operations.CancelAll();
        _session.Clear();
        ClearGuidedOptions();
        _resultWindow.Hide();
        _orb.ShowState(
            OrbPresentationState.Cancelled,
            ResourceText.Get("ContextOrbCancelledStatus"),
            ResourceText.Get("ContextOrbCancelledDetail"));
        _ = HideOrbAfterAsync(TimeSpan.FromMilliseconds(700));
    }

    public void DismissTransientResult()
    {
        if (_disposed)
        {
            return;
        }

        _operations.CancelAll();
        _resultWindow.Hide();
        _orb.Hide();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operations.Dispose();
        _resultWindow.UndoRequested -= OnUndoRequested;
        _resultWindow.InsertRequested -= OnInsertRequested;
        _resultWindow.TakeActionRequested -= OnTakeActionRequested;
        _resultWindow.MoreOptionsRequested -= OnMoreOptionsRequested;
        _resultWindow.RefineRequested -= OnRefineRequested;
        _resultWindow.CloseRequested -= OnCloseRequested;
        _orb.CancelRequested -= OnOrbCancelRequested;
        _orb.GuidedOperationRequested -= OnGuidedOperationRequested;
    }

    private async Task RunSafelyAsync()
    {
        try
        {
            await RunAsync();
        }
        catch (OperationCanceledException)
        {
            // The initiating cancellation path owns the visible state. Emergency
            // cancel shows a bounded confirmation; outside dismissal already hid it.
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowFailure(
                ResourceText.Get("ContextResultUnexpectedFailureTitle"),
                ResourceText.Get("ContextResultUnexpectedFailureMessage"));
        }
    }

    private async Task RunQuickTaskSafelyAsync(QuickTaskDefinition definition)
    {
        try
        {
            await RunQuickTaskAsync(definition);
        }
        catch (OperationCanceledException)
        {
            // The initiating cancellation path owns the visible state.
        }
        catch (ArgumentException)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("QuickTaskUnsupportedContextTitle"),
                ResourceText.Get("QuickTaskUnsupportedContextMessage"),
                InfoBarSeverity.Warning);
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowFailure(
                ResourceText.Get("ContextResultUnexpectedFailureTitle"),
                ResourceText.Get("ContextResultUnexpectedFailureMessage"));
        }
    }

    private async Task RunQuickTaskAsync(QuickTaskDefinition definition)
    {
        _operations.CancelAll();
        Guid operationValue = Guid.NewGuid();
        using OperationLease operation = _operations.Begin(operationValue);
        _session.Clear();
        ClearGuidedOptions();
        _resultWindow.Hide();
        ForegroundWindowIdentity? originalForeground = _foreground.GetCurrent();

        _orb.ShowState(
            OrbPresentationState.Thinking,
            ResourceText.Get("ContextOrbCapturingStatus"),
            ResourceText.Get("ContextOrbCancelHintText"));
        await _inputSettler.WaitForModifiersReleasedAsync(operation.Token);
        SelectionCaptureResult capture = await _capture.CaptureAsync(ContextLifetime, operation.Token);
        ContextExecutionInput input;
        if (capture.Status == SelectionCaptureStatus.Captured && capture.Context is not null)
        {
            input = ContextExecutionInput.FromText(capture.Context);
        }
        else if (capture.Status == SelectionCaptureStatus.NoSelection &&
                 definition.SupportedContext.HasFlag(QuickTaskContextType.Image))
        {
            _orb.Hide();
            RegionContextCaptureResult region = await _regionCapture.CaptureAsync(ContextLifetime, operation.Token);
            if (region.Status == RegionContextCaptureStatus.Cancelled)
            {
                return;
            }

            if (region.Input is null)
            {
                throw new ArgumentException("The requested image context was unavailable.", nameof(definition));
            }

            input = region.Input;
        }
        else if (capture.Status == SelectionCaptureStatus.NoSelection &&
                 definition.SupportedContext.HasFlag(QuickTaskContextType.Text))
        {
            _orb.Hide();
            var inputWindow = new QuickTaskInputWindow(
                definition.DisplayName,
                _resultWindow.CurrentTheme);
            string? typedText = await inputWindow.ShowAsync(operation.Token);
            if (typedText is null)
            {
                RestoreForeground(originalForeground);
                return;
            }

            TargetIdentity target = CreateTargetIdentity(originalForeground);
            ContextSnapshot typedContext = ContextSnapshot.FromText(
                ContextKind.Text,
                ContextSource.DirectInput,
                target,
                typedText,
                DateTimeOffset.UtcNow,
                ContextLifetime);
            input = ContextExecutionInput.FromText(typedContext);
        }
        else if (capture.Status == SelectionCaptureStatus.Cancelled)
        {
            _orb.Hide();
            return;
        }
        else
        {
            throw new ArgumentException("The Quick Task does not support the available context.", nameof(definition));
        }

        if (!definition.Supports(input.Context.Kind))
        {
            throw new ArgumentException("The Quick Task does not support the captured context.", nameof(definition));
        }

        _session.Begin(input);
        RestoreForeground(originalForeground);
        _orb.ShowState(
            OrbPresentationState.Generating,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                ResourceText.Get("QuickTaskRunningStatus"),
                definition.DisplayName),
            ResourceText.Get("QuickTaskRunningDetail"));
        ContextTriggerExecutionResult execution = await _trigger.ExecuteQuickTaskAsync(
            input,
            definition,
            _model,
            operation.Token);
        LastModelLatency = execution.Duration;
        if (execution.Status == ContextTriggerExecutionStatus.Cancelled)
        {
            _orb.Hide();
            return;
        }

        if (execution.Result is null)
        {
            _orb.Hide();
            _resultWindow.ShowFailure(
                ResourceText.Get("ContextResultProviderFailureTitle"),
                GetProviderFailureMessage(execution));
            return;
        }

        await PresentCompletedResultAsync(
            new OperationId(operationValue),
            input.Context,
            execution.Result,
            operation.Token,
            operationLabel: definition.DisplayName);
    }

    private async Task RunAsync()
    {
        _operations.CancelAll();
        Guid operationValue = Guid.NewGuid();
        using OperationLease operation = _operations.Begin(operationValue);
        _session.Clear();
        ClearGuidedOptions();
        _resultWindow.Hide();

        long invoked = Stopwatch.GetTimestamp();
        _orb.ShowState(
            OrbPresentationState.Thinking,
            ResourceText.Get("ContextOrbCapturingStatus"),
            ResourceText.Get("ContextOrbCancelHintText"));
        LastOrbPresentationLatency = Stopwatch.GetElapsedTime(invoked);

        await _inputSettler.WaitForModifiersReleasedAsync(operation.Token);

        SelectionCaptureResult capture = await _capture.CaptureAsync(ContextLifetime, operation.Token);
        LastCaptureLatency = capture.Duration;
        ContextExecutionInput input;
        DetectedColor? detectedColor = null;
        if (capture.Status == SelectionCaptureStatus.Captured && capture.Context is not null)
        {
            input = ContextExecutionInput.FromText(capture.Context);
        }
        else if (capture.Status == SelectionCaptureStatus.NoSelection)
        {
            _orb.Hide();
            RegionContextCaptureResult region = await _regionCapture.CaptureAsync(
                ContextLifetime,
                operation.Token);
            if (region.Status == RegionContextCaptureStatus.Cancelled)
            {
                _orb.Hide();
                return;
            }

            if (region.Input is null)
            {
                _resultWindow.ShowFailure(
                    ResourceText.Get("ContextResultNoSelectionTitle"),
                    string.IsNullOrWhiteSpace(region.SafeDetail)
                        ? GetCaptureFailureMessage(capture.Status)
                        : region.SafeDetail);
                return;
            }

            input = region.Input;
            detectedColor = region.Color;
        }
        else
        {
            if (capture.Status == SelectionCaptureStatus.Cancelled)
            {
                _orb.Hide();
                return;
            }

            _orb.Hide();
            _resultWindow.ShowFailure(
                ResourceText.Get("ContextResultNoSelectionTitle"),
                GetCaptureFailureMessage(capture.Status));
            return;
        }

        _session.Begin(input);
        ContextSnapshot context = input.Context;
        if (capture.Status == SelectionCaptureStatus.NoSelection)
        {
            nint sourceHandle = GetTargetHandle(context);
            if (sourceHandle != nint.Zero)
            {
                _ = _activator.TryActivate(sourceHandle);
            }
        }

        bool dataHubGrounding = IsDataHubCandidate(context.Text);
        _orb.ShowState(
            OrbPresentationState.Generating,
            dataHubGrounding
                ? ResourceText.Get("ContextOrbDataHubGroundingStatus")
                : GetCapturedContextStatus(context.Kind),
            dataHubGrounding
                ? ResourceText.Get("ContextOrbDataHubGroundingDetail")
                : ResourceText.Get("ContextOrbSmartModeDetail"));

        if (detectedColor is not null)
        {
            SmartResult colorResult = CreateColorResult(detectedColor);
            await PresentCompletedResultAsync(
                new OperationId(operationValue),
                context,
                colorResult,
                operation.Token,
                detectedColor.Hex,
                detectedColor);
            return;
        }

        if (_defaultMode == InteractionMode.Guided)
        {
            await ShowGuidedOptionsAsync(input, operation.Token);
            return;
        }

        ContextTriggerExecutionResult execution = await _trigger.ExecuteAsync(
            input,
            _model,
            operation.Token);
        LastModelLatency = execution.Duration;
        if (execution.Status == ContextTriggerExecutionStatus.Cancelled)
        {
            _orb.Hide();
            return;
        }

        if (execution.Result is null)
        {
            _orb.Hide();
            _resultWindow.ShowFailure(
                ResourceText.Get("ContextResultProviderFailureTitle"),
                GetProviderFailureMessage(execution));
            return;
        }

        if (execution.Status == ContextTriggerExecutionStatus.NeedsGuidedMode)
        {
            _session.SetResult(context.Fingerprint, execution.Result);
            await ShowGuidedOptionsAsync(input, operation.Token);
            return;
        }

        await PresentCompletedResultAsync(
            new OperationId(operationValue),
            context,
            execution.Result,
            operation.Token);
    }

    private async void OnCopyRequested(object? sender, EventArgs args)
    {
        ContextSessionAccess session = _session.GetCurrent();
        if (session.LatestResult is null)
        {
            return;
        }

        ResultClipboardWriteResult copy = await _clipboard.CopyTextAsync(
            session.ClipboardText ?? session.LatestResult.FinalContent);
        ShowCopyNotice(copy);
    }

    private async void OnReplaceRequested(object? sender, EventArgs args)
    {
        try
        {
            await ReplaceAsync();
        }
        catch (OperationCanceledException)
        {
            // Preserve the state selected by Cancel or transient dismissal.
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultReplaceFailedTitle"),
                ResourceText.Get("ContextResultReplaceFailedMessage"),
                InfoBarSeverity.Error);
        }
    }

    private async void OnInsertRequested(object? sender, EventArgs args)
    {
        try
        {
            ContextSnapshot? context = _session.GetCurrent().Context;
            if (context is { Text: not null } &&
                context.Source is not ContextSource.DirectInput)
            {
                await ReplaceAsync();
            }
            else
            {
                await InsertAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Preserve the state selected by Cancel or transient dismissal.
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultInsertFailedTitle"),
                ResourceText.Get("ContextResultInsertFailedMessage"),
                InfoBarSeverity.Error);
        }
    }

    private void OnTakeActionRequested(object? sender, EventArgs args)
    {
        SmartResult? latest = _session.GetCurrent().LatestResult;
        switch (latest?.SuggestedAction.Type)
        {
            case SuggestedActionType.Copy:
                OnCopyRequested(sender, args);
                break;
            case SuggestedActionType.InsertText:
                OnInsertRequested(sender, args);
                break;
            case SuggestedActionType.ReplaceSelection:
                OnReplaceRequested(sender, args);
                break;
            case SuggestedActionType.FillForm:
            case SuggestedActionType.BrowserPlan:
                BrowserTakeActionRequested?.Invoke(
                    string.IsNullOrWhiteSpace(latest.SuggestedAction.Summary)
                        ? latest.FinalContent
                        : latest.SuggestedAction.Summary);
                break;
        }
    }

    private async Task ShowGuidedOptionsAsync(
        ContextExecutionInput input,
        CancellationToken cancellationToken)
    {
        ContextSessionAccess session = _session.GetCurrent();
        if (session.Context?.Fingerprint != input.Context.Fingerprint)
        {
            return;
        }

        if (_guidedOptionsFingerprint == input.Context.Fingerprint && _guidedOptions is not null)
        {
            _orb.ShowGuidedOptions(_guidedOptions);
            return;
        }

        _orb.ShowState(
            OrbPresentationState.Thinking,
            "Tailoring options",
            "Finding useful actions for this context");
        Guid operationValue = Guid.NewGuid();
        using OperationLease operation = _operations.Begin(operationValue);
        GuidedOptionsExecutionResult generated = await _trigger.GenerateGuidedOptionsAsync(
            input,
            _model,
            operation.Token);
        LastModelLatency = generated.Duration;
        if (generated.Status == ContextTriggerExecutionStatus.Cancelled)
        {
            _orb.Hide();
            return;
        }

        if (generated.Options is null || generated.Status != ContextTriggerExecutionStatus.Completed)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultProviderFailureTitle"),
                GetProviderFailureMessage(new ContextTriggerExecutionResult(
                    generated.Status,
                    null,
                    generated.Failure,
                    generated.Model,
                    generated.Duration)),
                InfoBarSeverity.Error);
            return;
        }

        session = _session.GetCurrent();
        if (session.Context?.Fingerprint != input.Context.Fingerprint)
        {
            return;
        }

        _guidedOptions = generated.Options;
        _guidedOptionsFingerprint = input.Context.Fingerprint;
        _orb.ShowGuidedOptions(_guidedOptions);
    }

    private void ClearGuidedOptions()
    {
        _guidedOptions = null;
        _guidedOptionsFingerprint = null;
    }

    private async void OnMoreOptionsRequested(object? sender, EventArgs args)
    {
        try
        {
            ContextSessionAccess session = _session.GetCurrent();
            if (session.Status != ContextSessionAccessStatus.Available || session.Input is null)
            {
                _orb.Hide();
                _resultWindow.ShowNotice(
                    ResourceText.Get("ContextResultContextExpiredTitle"),
                    ResourceText.Get("ContextResultContextExpiredMessage"),
                    InfoBarSeverity.Error);
                return;
            }

            _resultWindow.Hide();
            await ShowGuidedOptionsAsync(session.Input, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A newer interaction owns the visible state.
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultProviderFailureTitle"),
                ResourceText.Get("ContextResultProviderFailureMessage"),
                InfoBarSeverity.Error);
        }
    }

    private async void OnRefineRequested(object? sender, EventArgs args)
    {
        try
        {
            ContextSessionAccess session = _session.GetCurrent();
            if (session.Status != ContextSessionAccessStatus.Available ||
                session.Context is null || session.LatestResult is null)
            {
                _resultWindow.ShowNotice(
                    ResourceText.Get("ContextResultContextExpiredTitle"),
                    ResourceText.Get("ContextResultContextExpiredMessage"),
                    InfoBarSeverity.Error);
                return;
            }

            OverlayRectangle anchor = _resultWindow.OverlayBounds;
            SmartResult current = session.LatestResult;
            _resultWindow.Hide();
            var inputWindow = new QuickTaskInputWindow(
                ResourceText.Get("ContextResultRefineInputTitle"),
                _resultWindow.CurrentTheme,
                ResourceText.Get("ContextResultRefineInputPlaceholder"));
            string? request = await inputWindow.ShowAsync(CancellationToken.None, anchor);
            if (string.IsNullOrWhiteSpace(request))
            {
                _resultWindow.ShowResult(current, guidedMode: false);
                return;
            }

            if (request.Length > 4_000)
            {
                _resultWindow.ShowResult(current, guidedMode: false);
                _resultWindow.ShowNotice(
                    ResourceText.Get("ContextResultRefineTooLongTitle"),
                    ResourceText.Get("ContextResultRefineTooLongMessage"),
                    InfoBarSeverity.Warning);
                return;
            }

            string currentContent = current.FinalContent.Length <= 50_000
                ? current.FinalContent
                : current.FinalContent[..50_000];
            var option = new GuidedOption(
                "refine_result",
                ResourceText.Get("ContextResultRefiningLabel"),
                $"""
                Improve the current generated result according to the user's refinement request while preserving the original captured context. Return the complete refined result only.

                <user_refinement_request>
                {request.Trim()}
                </user_refinement_request>

                <current_generated_result>
                {currentContent}
                </current_generated_result>
                """);
            await RunGuidedAsync(option);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultUnexpectedFailureTitle"),
                ResourceText.Get("ContextResultUnexpectedFailureMessage"),
                InfoBarSeverity.Error);
        }
    }

    private async void OnGuidedOperationRequested(
        object? sender,
        GuidedOperationRequestedEventArgs args)
    {
        try
        {
            GuidedOption option = args.Option;
            if (option.IsCustomTask)
            {
                bool cancelled;
                using (OperationLease prompt = _operations.Begin(Guid.NewGuid()))
                {
                    var inputWindow = new QuickTaskInputWindow(
                        ResourceText.Get("GuidedOperationCustomTask"),
                        _resultWindow.CurrentTheme);
                    string? customInstruction = await inputWindow.ShowAsync(
                        prompt.Token,
                        _orb.OverlayBounds);
                    cancelled = prompt.Token.IsCancellationRequested;
                    if (!string.IsNullOrWhiteSpace(customInstruction))
                    {
                        option = new GuidedOption(
                            "custom_task",
                            ResourceText.Get("GuidedOperationCustomTask"),
                            customInstruction);
                    }
                }

                if (option.IsCustomTask)
                {
                    ContextSessionAccess session = _session.GetCurrent();
                    if (!cancelled && session.Context is not null &&
                        _guidedOptionsFingerprint == session.Context.Fingerprint && _guidedOptions is not null)
                    {
                        _orb.ShowGuidedOptions(_guidedOptions);
                    }

                    return;
                }
            }

            await RunGuidedAsync(option);
        }
        catch (OperationCanceledException)
        {
            // Preserve the state selected by Cancel or transient dismissal.
        }
        catch (ArgumentException)
        {
            _orb.ShowState(
                OrbPresentationState.Error,
                ResourceText.Get("ContextResultProviderFailureTitle"),
                ResourceText.Get("ContextResultProviderFailureMessage"));
        }
        catch (Exception)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultUnexpectedFailureTitle"),
                ResourceText.Get("ContextResultUnexpectedFailureMessage"),
                InfoBarSeverity.Error);
        }
    }

    private async Task RunGuidedAsync(GuidedOption option)
    {
        ContextSessionAccess session = _session.GetCurrent();
        if (session.Status != ContextSessionAccessStatus.Available || session.Context is null)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultContextExpiredTitle"),
                ResourceText.Get("ContextResultContextExpiredMessage"),
                InfoBarSeverity.Error);
            return;
        }

        ContextSnapshot context = session.Context;
        ContextExecutionInput input = session.Input!;
        _operations.CancelAll();
        Guid operationValue = Guid.NewGuid();
        using OperationLease operation = _operations.Begin(operationValue);
        _orb.ShowState(
            OrbPresentationState.Generating,
            option.Label,
            ResourceText.Get("ContextOrbCancelHintText"));

        ContextTriggerExecutionResult execution = await _trigger.ExecuteGuidedInstructionAsync(
            input,
            option,
            _model,
            operation.Token);
        LastModelLatency = execution.Duration;
        if (execution.Status == ContextTriggerExecutionStatus.Cancelled)
        {
            _orb.Hide();
            return;
        }

        if (execution.Result is null)
        {
            _orb.Hide();
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultProviderFailureTitle"),
                GetProviderFailureMessage(execution),
                InfoBarSeverity.Error);
            return;
        }

        await PresentCompletedResultAsync(
            new OperationId(operationValue),
            context,
            execution.Result,
            operation.Token,
            operationLabel: option.Label);
    }

    private async Task PresentCompletedResultAsync(
        OperationId operationId,
        ContextSnapshot context,
        SmartResult result,
        CancellationToken cancellationToken,
        string? clipboardText = null,
        DetectedColor? detectedColor = null,
        string? operationLabel = null)
    {
        ContextSessionAccess session = _session.GetCurrent();
        if (session.Context?.Fingerprint != context.Fingerprint)
        {
            return;
        }

        _session.SetResult(context.Fingerprint, result, clipboardText);
        string effectiveOperationLabel = operationLabel ??
            result.OperationLabel ??
            ToSmartOperationLabel(result.Intent);
        if (detectedColor is null)
        {
            _orb.ShowState(
                OrbPresentationState.Generating,
                effectiveOperationLabel,
                ResourceText.Get("ContextOrbFinalizingDetail"));
            await Task.Delay(TimeSpan.FromMilliseconds(360), cancellationToken);
        }

        _orb.ShowState(
            OrbPresentationState.Done,
            ResourceText.Get("ContextOrbReadyStatus"),
            ResourceText.Get("ContextOrbReadyDetail"));
        if (detectedColor is null)
        {
            _resultWindow.ShowResult(
                result,
                guidedMode: false,
                effectiveOperationLabel,
                canReplace: context.Source is not ContextSource.DirectInput &&
                            context.Kind is not ContextKind.Image and not ContextKind.UserInterface);
        }

        else
        {
            _resultWindow.ShowColorResult(result, detectedColor);
        }

        _resultWindow.SetUndoAvailable(_undoHistory.Count > 0);

        string textToCopy = _session.GetCurrent().ClipboardText ?? result.FinalContent;
        ResultClipboardWriteResult copy = await _autoCopy.CopyFinalOnceAsync(
            operationId,
            textToCopy,
            cancellationToken);
        ShowCopyNotice(copy);
        _orb.Hide();
    }

    private static SmartResult CreateColorResult(DetectedColor color) => new(
        ContextKind.Image,
        SmartIntent.Identify,
        1d,
        $"{color.Hex}\nRGB ({color.Red}, {color.Green}, {color.Blue})\n{color.ApproximateName}",
        new SuggestedAction(SuggestedActionType.Copy, "Copy the detected hex color"),
        Cursivis.Domain.Actions.RiskLevel.None,
        ConfirmationHint.None);

    private static string ToSmartOperationLabel(SmartIntent intent) => intent switch
    {
        SmartIntent.Translate => "Translating",
        SmartIntent.Reply => "Drafting reply",
        SmartIntent.Rewrite => "Improving writing",
        SmartIntent.Summarize => "Summarizing",
        SmartIntent.Explain => "Explaining",
        SmartIntent.Debug => "Explaining error",
        SmartIntent.Extract => "Extracting information",
        SmartIntent.Identify => "Identifying",
        SmartIntent.Answer => "Answering",
        _ => "Analyzing context",
    };

    private static TargetIdentity CreateTargetIdentity(ForegroundWindowIdentity? foreground) => new(
        foreground is null || string.IsNullOrWhiteSpace(foreground.ProcessName)
            ? "unknown-source"
            : foreground.ProcessName,
        foreground?.WindowHandle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture)
            ?? "0");

    private void RestoreForeground(ForegroundWindowIdentity? foreground)
    {
        if (foreground?.WindowHandle is { } handle && handle != nint.Zero)
        {
            _ = _activator.TryActivate(handle);
        }
    }

    private void ShowCopyNotice(ResultClipboardWriteResult copy)
    {
        if (copy.Status == ResultClipboardWriteStatus.Cancelled)
        {
            return;
        }

        _resultWindow.ShowNotice(
            copy.Status == ResultClipboardWriteStatus.Copied
                ? ResourceText.Get("ContextResultCopiedTitle")
                : ResourceText.Get("ContextResultClipboardUnavailableTitle"),
            copy.Status == ResultClipboardWriteStatus.Copied
                ? ResourceText.Get("ContextResultCopiedMessage")
                : ResourceText.Get("ContextResultClipboardUnavailableMessage"),
            copy.Status == ResultClipboardWriteStatus.Copied
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error);
    }

    private async Task ReplaceAsync()
    {
        ContextSessionAccess session = _session.GetCurrent();
        ContextSnapshot? currentContext = session.Context;
        SmartResult? currentResult = session.LatestResult;
        if (currentContext is null || currentResult is null ||
            !long.TryParse(
                currentContext.Target.WindowId,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out long handleValue))
        {
            return;
        }

        _resultWindow.Hide();
        _orb.ShowState(
            OrbPresentationState.Executing,
            ResourceText.Get("ContextOrbReplacingStatus"),
            ResourceText.Get("ContextOrbCancelHintText"));

        nint targetHandle = new(handleValue);
        if (!_activator.TryActivate(targetHandle))
        {
            _orb.Hide();
            _resultWindow.ShowResult(currentResult, guidedMode: false);
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultReplaceFailedTitle"),
                ResourceText.Get("ContextResultTargetUnavailableMessage"),
                InfoBarSeverity.Error);
            return;
        }

        await Task.Delay(80);
        TextReplacementResult replacement = await _replacement.ReplaceAsync(
            currentContext,
            currentResult.FinalContent);
        if (replacement.Status == TextReplacementStatus.Replaced)
        {
            _undoHistory.Push(currentContext);
            _orb.ShowState(
                OrbPresentationState.Done,
                ResourceText.Get("ContextOrbReplacedStatus"),
                ResourceText.Get("ContextOrbReplacedDetail"));
            if (_closeResultsAfterInsert)
            {
                _session.Clear();
                await HideOrbAfterAsync(TimeSpan.FromMilliseconds(900));
            }
            else
            {
                _orb.Hide();
                _resultWindow.ShowResult(currentResult, guidedMode: false);
                _resultWindow.SetUndoAvailable(true);
            }
            return;
        }

        _orb.Hide();
        _resultWindow.ShowResult(currentResult, guidedMode: false);
        _resultWindow.ShowNotice(
            ResourceText.Get("ContextResultReplaceFailedTitle"),
            replacement.Status == TextReplacementStatus.StaleTarget
                ? ResourceText.Get("ContextResultTargetUnavailableMessage")
                : ResourceText.Get("ContextResultReplaceFailedMessage"),
            InfoBarSeverity.Error);
    }

    private async Task InsertAsync()
    {
        ContextSessionAccess session = _session.GetCurrent();
        ContextSnapshot? currentContext = session.Context;
        SmartResult? currentResult = session.LatestResult;
        if (currentContext is null || currentResult is null ||
            !long.TryParse(
                currentContext.Target.WindowId,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out long handleValue))
        {
            return;
        }

        _resultWindow.Hide();
        _orb.ShowState(
            OrbPresentationState.Executing,
            ResourceText.Get("ContextOrbInsertingStatus"),
            ResourceText.Get("ContextOrbCancelHintText"));

        nint targetHandle = new(handleValue);
        if (!_activator.TryActivate(targetHandle))
        {
            ShowInsertionFailure(currentResult, targetUnavailable: true);
            return;
        }

        await Task.Delay(80);
        TextInsertionResult insertion = await _insertion.InsertAsync(
            currentContext,
            currentResult.FinalContent);
        if (insertion.Status == TextInsertionStatus.Inserted)
        {
            _orb.ShowState(
                OrbPresentationState.Done,
                ResourceText.Get("ContextOrbInsertedStatus"),
                ResourceText.Get("ContextOrbInsertedDetail"));
            if (_closeResultsAfterInsert)
            {
                _session.Clear();
                await HideOrbAfterAsync(TimeSpan.FromMilliseconds(900));
            }
            else
            {
                _orb.Hide();
                _resultWindow.ShowResult(currentResult, guidedMode: false);
                _resultWindow.SetUndoAvailable(_undoHistory.Count > 0);
            }
            return;
        }

        ShowInsertionFailure(
            currentResult,
            insertion.Status == TextInsertionStatus.StaleTarget);
    }

    private void ShowInsertionFailure(SmartResult currentResult, bool targetUnavailable)
    {
        _orb.Hide();
        _resultWindow.ShowResult(currentResult, guidedMode: false);
        _resultWindow.ShowNotice(
            ResourceText.Get("ContextResultInsertFailedTitle"),
            targetUnavailable
                ? ResourceText.Get("ContextResultTargetUnavailableMessage")
                : ResourceText.Get("ContextResultInsertFailedMessage"),
            InfoBarSeverity.Error);
    }

    private async void OnUndoRequested(object? sender, EventArgs args)
    {
        if (_externalUndoAvailable)
        {
            ExternalUndoRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_undoHistory.Count == 0)
        {
            _resultWindow.SetUndoAvailable(false);
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultNothingToUndoTitle"),
                ResourceText.Get("ContextResultNothingToUndoMessage"),
                InfoBarSeverity.Warning);
            return;
        }

        ContextSnapshot context = _undoHistory.Peek();
        nint targetHandle = GetTargetHandle(context);
        _resultWindow.Hide();
        _orb.ShowState(
            OrbPresentationState.Executing,
            ResourceText.Get("ContextOrbUndoingStatus"),
            ResourceText.Get("ContextOrbCancelHintText"));
        if (targetHandle == nint.Zero || !_activator.TryActivate(targetHandle))
        {
            ShowUndoFailure(ResourceText.Get("ContextResultTargetUnavailableMessage"));
            return;
        }

        await Task.Delay(50);
        TextUndoResult undo = await _undo.UndoAsync(context);
        if (undo.Status == TextUndoStatus.Undone)
        {
            _ = _undoHistory.Pop();
            _resultWindow.SetUndoAvailable(_undoHistory.Count > 0);
            _orb.ShowState(
                OrbPresentationState.Done,
                ResourceText.Get("ContextOrbUndoAppliedStatus"),
                ResourceText.Get("ContextOrbUndoAppliedDetail"));
            _resultWindow.ShowNotice(
                ResourceText.Get("ContextResultUndoAppliedTitle"),
                ResourceText.Get("ContextResultUndoAppliedMessage"),
                InfoBarSeverity.Success);
            _orb.Hide();
            return;
        }

        if (undo.Status != TextUndoStatus.Cancelled)
        {
            ShowUndoFailure(undo.SafeDetail);
        }
    }

    private void ShowUndoFailure(string safeDetail)
    {
        _orb.Hide();
        _resultWindow.SetUndoAvailable(_undoHistory.Count > 0);
        _resultWindow.ShowNotice(
            ResourceText.Get("ContextResultUndoFailedTitle"),
            string.IsNullOrWhiteSpace(safeDetail)
                ? ResourceText.Get("ContextResultUndoFailedMessage")
                : safeDetail,
            InfoBarSeverity.Error);
    }

    private void OnCloseRequested(object? sender, EventArgs args)
    {
        nint targetHandle = GetCurrentTargetHandle();
        _resultWindow.Hide();
        _orb.Hide();
        if (targetHandle != nint.Zero)
        {
            _ = _activator.TryActivate(targetHandle);
        }
    }

    private void OnOrbCancelRequested(object? sender, EventArgs args) => Cancel();

    private static string GetCapturedContextStatus(ContextKind kind) => kind switch
    {
        ContextKind.Image => ResourceText.Get("ContextOrbImageCapturedStatus"),
        ContextKind.Code => ResourceText.Get("ContextOrbCodeCapturedStatus"),
        _ => ResourceText.Get("ContextOrbTextCapturedStatus"),
    };

    private static bool IsDataHubCandidate(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains(" from ", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" join ", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("select ", StringComparison.OrdinalIgnoreCase));

    private nint GetCurrentTargetHandle()
    {
        ContextSnapshot? context = _session.GetCurrent().Context;
        return context is null ? nint.Zero : GetTargetHandle(context);
    }

    private static nint GetTargetHandle(ContextSnapshot context)
    {
        return long.TryParse(
            context.Target.WindowId,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out long value)
            ? new nint(value)
            : nint.Zero;
    }

    private async Task HideOrbAfterAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        if (!_disposed)
        {
            _orb.Hide();
        }
    }

    private static string GetCaptureFailureMessage(SelectionCaptureStatus status) => status switch
    {
        SelectionCaptureStatus.ForegroundChanged => ResourceText.Get("ContextResultForegroundChangedMessage"),
        SelectionCaptureStatus.Unavailable => ResourceText.Get("ContextResultCaptureUnavailableMessage"),
        SelectionCaptureStatus.TimedOut => ResourceText.Get("ContextResultCaptureTimedOutMessage"),
        _ => ResourceText.Get("ContextResultNoSelectionMessage"),
    };

    private static string GetProviderFailureMessage(ContextTriggerExecutionResult execution)
    {
        if (execution.Status == ContextTriggerExecutionStatus.MalformedResponse)
        {
            return ResourceText.Get("ContextResultMalformedResponseMessage");
        }

        return execution.Failure?.Kind switch
        {
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.Authentication =>
                ResourceText.Get("ContextResultAuthenticationMessage"),
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.Permission =>
                ResourceText.Get("ContextResultPermissionMessage"),
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.Quota =>
                ResourceText.Get("ContextResultQuotaMessage"),
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.RateLimit =>
                ResourceText.Get("ContextResultRateLimitedMessage"),
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.ModelUnavailable =>
                ResourceText.Get("ContextResultModelUnavailableMessage"),
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.Network =>
                ResourceText.Get("ContextResultNetworkMessage"),
            Cursivis.Contracts.OpenAI.OpenAiFailureKind.Timeout =>
                ResourceText.Get("ContextResultTimeoutMessage"),
            _ => ResourceText.Get("ContextResultProviderFailureMessage"),
        };
    }
}
