using Cursivis.Application.Actions;
using Cursivis.Contracts.Browser;
using Cursivis.Domain.Actions;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Cursivis.Windows.App;

public sealed record ActionPlanReviewResult(bool Confirmed, string? AdditionalInstruction);

public sealed partial class ActionPlanReviewWindow : Window
{
    private const int LogicalWidth = 460;
    private readonly int _logicalHeight;
    private readonly TransparentWindowBackdrop _transparentBackdrop;
    private readonly NativeOverlayWindow _overlay;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly TaskCompletionSource<ActionPlanReviewResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private bool _wasActivated;

    public ActionPlanReviewWindow(
        BrowserActionProposal proposal,
        PolicyDecision policy,
        ElementTheme theme = ElementTheme.Default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(policy);
        InitializeComponent();
        OverlayRoot.RequestedTheme = theme;
        Title = "Cursivis Take Action confirmation";
        GoalText.Text = proposal.Plan.Goal;
        PolicyText.Text = DescribePolicy(policy);
        ConfirmButton.Content = policy.EffectiveRisk >= RiskLevel.High
            ? "Confirm & run"
            : "Run action";
        BuildSteps(proposal);
        _logicalHeight = Math.Clamp(238 + (proposal.Commands.Length * 38), 300, 500);

        _transparentBackdrop = new TransparentWindowBackdrop(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragHeader);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClientPointerSource.ExitedMoveSize += OnNativeMoveSizeExited;
        _overlay = new NativeOverlayWindow(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            noActivate: false);
        Activated += OnActivated;
        Closed += OnClosed;
    }

    public async Task<ActionPlanReviewResult> ShowAsync(
        OverlayRectangle anchor,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            _ = DispatcherQueue.TryEnqueue(() => Complete(false));
        });
        if (cancellationToken.IsCancellationRequested)
        {
            return new(false, null);
        }

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        OverlayRectangle bounds = NativeWindowPositioner.GetAdjacentPlacement(
            handle,
            LogicalWidth,
            _logicalHeight,
            anchor);
        AppWindow.Show(activateWindow: false);
        _overlay.Show(bounds, topmost: true, noActivate: false, cornerRadius: 24);
        _ = _overlay.ActivateForInteraction();
        _ = DispatcherQueue.TryEnqueue(_overlay.EnforceBorderlessStyle);
        _ = ConfirmButton.Focus(FocusState.Programmatic);
        return await _completion.Task;
    }

    private void BuildSteps(BrowserActionProposal proposal)
    {
        Dictionary<string, string> labels = proposal.Form.Fields
            .Select(field => KeyValuePair.Create(field.StableTargetId, field.Label))
            .Concat(proposal.Form.Controls.Select(control => KeyValuePair.Create(control.StableTargetId, control.Label)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        StepsList.ItemsSource = proposal.Commands.Select((command, index) =>
            $"{index + 1}. {DescribeCommand(command, labels.GetValueOrDefault(command.StableTargetId) ?? "Visible control")}").ToArray();
    }

    private static string DescribeCommand(BrowserActionCommandSpec command, string label) => command.Kind switch
    {
        BrowserCommandKind.SetValue => $"Set {label} to “{command.Value}”",
        BrowserCommandKind.SetChecked => $"Set {label} to {(command.Checked == true ? "checked" : "not checked")}",
        BrowserCommandKind.SelectOption => $"Choose “{command.Value}” for {label}",
        BrowserCommandKind.Focus => $"Focus {label}",
        BrowserCommandKind.Highlight => $"Show {label}",
        BrowserCommandKind.ClickNavigation => $"Open {label}",
        BrowserCommandKind.SubmitSearch => $"Search using {label}",
        BrowserCommandKind.Submit => $"Submit using {label}",
        _ => label,
    };

    private static string DescribePolicy(PolicyDecision policy) => policy.EffectiveRisk switch
    {
        RiskLevel.High => "High-impact step detected. This confirmation is bound to this exact plan and expires shortly.",
        RiskLevel.Medium => "Cursivis will verify every field change and stop if the page no longer matches.",
        _ => "Only the listed visible controls will be changed, with verification after each step.",
    };

    private void OnConfirmClicked(object sender, RoutedEventArgs args) => Complete(true);

    private void OnCancelClicked(object sender, RoutedEventArgs args) => Complete(false);

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Complete(false);
    }

    private void OnConfirmAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Complete(true);
    }

    private void OnNativeMoveSizeExited(InputNonClientPointerSource sender, ExitedMoveSizeEventArgs args)
    {
        OverlayRectangle clamped = NativeWindowPositioner.ClampCurrentPlacement(
            _overlay.WindowHandle,
            _overlay.GetBounds());
        _overlay.Show(clamped, topmost: true, noActivate: false, cornerRadius: 24);
    }

    private void Complete(bool confirmed)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        string? instruction = string.IsNullOrWhiteSpace(InstructionsTextBox.Text)
            ? null
            : InstructionsTextBox.Text.Trim();
        _overlay.Hide();
        _completion.TrySetResult(new(confirmed, instruction));
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _wasActivated)
        {
            Complete(false);
            return;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _wasActivated = true;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        _nonClientPointerSource.ExitedMoveSize -= OnNativeMoveSizeExited;
        _nonClientPointerSource.ClearAllRegionRects();
        if (!_completed)
        {
            _completed = true;
            _completion.TrySetResult(new(false, null));
        }

        _transparentBackdrop.Dispose();
        _overlay.Dispose();
    }
}
