using System.ComponentModel;
using Cursivis.Application.Context;
using Cursivis.Application.QuickTasks;
using Cursivis.Domain.QuickTasks;
using Cursivis.Windows.App.Controls;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.App.ViewModels;
using Cursivis.Windows.Platform.Hotkeys;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class QuickTaskPage : Page
{
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;

    internal QuickTaskSettingsViewModel ViewModel { get; } = new();

    public QuickTaskPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnUnloaded;
        if (App.CurrentRuntime is { } runtime)
        {
            ViewModel.Load(runtime.CurrentQuickTask);
            string activeChord = runtime.QuickTaskActiveChord;
            if (!string.IsNullOrWhiteSpace(activeChord))
            {
                QuickTaskHotkeyRecorder.ConfiguredChord = activeChord;
                QuickTaskHotkeyRecorder.ActiveChord = activeChord;
                QuickTaskHotkeyRecorder.StatusText = ResourceText.Get("HotkeyRegisteredStatus");
            }
        }

        UpdateSaveState();
    }

    private async void OnQuickTaskHotkeyCandidateCaptured(object? sender, HotkeyCandidateEventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            QuickTaskHotkeyRecorder.SetExternalValidation(ResourceText.Get("QuickTaskRuntimeUnavailable"));
            return;
        }

        try
        {
            HotkeyUpdateResult result = await runtime.UpdateQuickTaskHotkeyAsync(args.Chord);
            if (result.Status is HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive)
            {
                string active = result.ActiveChord?.ToString() ?? args.Chord;
                QuickTaskHotkeyRecorder.ConfiguredChord = active;
                QuickTaskHotkeyRecorder.ActiveChord = active;
                QuickTaskHotkeyRecorder.StatusText = ResourceText.Get("HotkeyRegisteredStatus");
                ShowOperationSuccess(ResourceText.Get("QuickTaskHotkeySaved"));
                return;
            }

            RestoreActiveQuickTaskChord(runtime, ResourceText.Get("QuickTaskHotkeyConflict"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            RestoreActiveQuickTaskChord(runtime, exception.Message);
        }
    }

    private void RestoreActiveQuickTaskChord(AppRuntime runtime, string message)
    {
        string active = runtime.QuickTaskActiveChord;
        QuickTaskHotkeyRecorder.ConfiguredChord = active;
        QuickTaskHotkeyRecorder.ActiveChord = active;
        QuickTaskHotkeyRecorder.SetExternalValidation(message);
        ShowOperationError(message);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.IsApproved) &&
            ApprovalCheckBox.IsChecked != ViewModel.IsApproved)
        {
            ApprovalCheckBox.IsChecked = ViewModel.IsApproved;
        }

        UpdateSaveState();
    }

    private void OnApprovalChanged(object sender, RoutedEventArgs args)
    {
        ViewModel.IsApproved = ApprovalCheckBox.IsChecked == true;
        UpdateSaveState();
    }

    private async void OnGenerateInstructionClicked(object sender, RoutedEventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            ShowOperationError(ResourceText.Get("QuickTaskRuntimeUnavailable"));
            return;
        }

        BeginOperation(ResourceText.Get("QuickTaskFinalizingStatus"));
        try
        {
            QuickTaskFinalizationResult result = await runtime.FinalizeQuickTaskAsync(
                ViewModel.DisplayName,
                ViewModel.RoughDescription,
                _operationCancellation!.Token);
            if (result.Draft is not null)
            {
                ViewModel.ApplyDraft(result.Draft);
            }

            switch (result.Status)
            {
                case QuickTaskFinalizationStatus.Completed:
                    ShowOperationSuccess(ResourceText.Get("QuickTaskFinalizedStatus"));
                    break;
                case QuickTaskFinalizationStatus.Rejected:
                    ShowOperationError(ResourceText.Get("QuickTaskFinalizationRejected"));
                    break;
                case QuickTaskFinalizationStatus.Cancelled:
                    OperationInfo.IsOpen = false;
                    break;
                default:
                    ShowOperationError(result.Failure?.SafeMessage ?? ResourceText.Get("QuickTaskFinalizationFailed"));
                    break;
            }
        }
        catch (ArgumentException exception)
        {
            ShowOperationError(exception.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnTestQuickTaskClicked(object sender, RoutedEventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            ShowOperationError(ResourceText.Get("QuickTaskRuntimeUnavailable"));
            return;
        }

        string fixture = QuickTaskFixtureInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(fixture))
        {
            ShowOperationError(ResourceText.Get("QuickTaskFixtureRequired"));
            return;
        }

        if (!ViewModel.IsDefinitionValid)
        {
            ShowOperationError(ViewModel.ValidationSummary);
            return;
        }

        BeginOperation(ResourceText.Get("QuickTaskTestingStatus"));
        try
        {
            QuickTaskDefinition definition = ViewModel.CreateDefinition(isExplicitlyApproved: true);
            ContextTriggerExecutionResult execution = await runtime.TestQuickTaskAsync(
                definition,
                fixture,
                _operationCancellation!.Token);
            if (execution.Result is null)
            {
                ShowOperationError(execution.Failure?.SafeMessage ?? ResourceText.Get("QuickTaskTestFailed"));
                return;
            }

            QuickTaskTestResult.Text = execution.Result.FinalContent;
            QuickTaskTestResult.Visibility = Visibility.Visible;
            ShowOperationSuccess(ResourceText.Get("QuickTaskTestSucceeded"));
        }
        catch (OperationCanceledException)
        {
            OperationInfo.IsOpen = false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShowOperationError(exception.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnSaveRequested(object? sender, EventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null || !ViewModel.IsDefinitionValid || !ViewModel.IsApproved)
        {
            return;
        }

        BeginOperation(ResourceText.Get("QuickTaskSavingStatus"));
        try
        {
            QuickTaskDefinition definition = ViewModel.CreateDefinition(isExplicitlyApproved: true);
            await runtime.SaveQuickTaskAsync(definition, _operationCancellation!.Token);
            ViewModel.MarkSaved(definition);
            ShowOperationSuccess(ResourceText.Get("QuickTaskSavedStatus"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowOperationError(exception.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnRestorePromptOptimizerClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.RestorePromptOptimizer();
        ApprovalCheckBox.IsChecked = false;
        QuickTaskTestResult.Visibility = Visibility.Collapsed;
    }

    private void OnDiscardRequested(object? sender, EventArgs args)
    {
        ViewModel.DiscardChanges();
        ApprovalCheckBox.IsChecked = ViewModel.IsApproved;
        OperationInfo.IsOpen = false;
        QuickTaskTestResult.Visibility = Visibility.Collapsed;
    }

    private void BeginOperation(string status)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _isBusy = true;
        OperationInfo.Title = status;
        OperationInfo.Message = string.Empty;
        OperationInfo.Severity = InfoBarSeverity.Informational;
        OperationInfo.IsOpen = true;
        UpdateSaveState();
    }

    private void EndOperation()
    {
        _isBusy = false;
        UpdateSaveState();
    }

    private void ShowOperationSuccess(string message)
    {
        OperationInfo.Title = message;
        OperationInfo.Message = string.Empty;
        OperationInfo.Severity = InfoBarSeverity.Success;
        OperationInfo.IsOpen = true;
    }

    private void ShowOperationError(string message)
    {
        OperationInfo.Title = ResourceText.Get("QuickTaskOperationFailedTitle");
        OperationInfo.Message = message;
        OperationInfo.Severity = InfoBarSeverity.Error;
        OperationInfo.IsOpen = true;
    }

    private void UpdateSaveState()
    {
        if (SaveState is null)
        {
            return;
        }

        SaveState.IsDirty = ViewModel.IsDirty;
        SaveState.CanSave = !_isBusy && ViewModel.IsDefinitionValid && ViewModel.IsApproved;
        SaveState.StateText = !ViewModel.IsDefinitionValid
            ? ViewModel.ValidationSummary
            : ViewModel.IsApproved
                ? ResourceText.Get("QuickTaskReadyToSave")
                : ResourceText.Get("QuickTaskApprovalRequired");
        GenerateInstructionButton.IsEnabled = !_isBusy;
        TestQuickTaskButton.IsEnabled = !_isBusy;
        ValidationInfo.Severity = ViewModel.IsDefinitionValid
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Error;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }
}
