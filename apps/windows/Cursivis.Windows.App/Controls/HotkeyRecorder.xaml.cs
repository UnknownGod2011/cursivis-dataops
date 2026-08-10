using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Cursivis.Windows.App.Controls;

public sealed partial class HotkeyRecorder : UserControl
{
    public static readonly DependencyProperty FunctionNameProperty = DependencyProperty.Register(
        nameof(FunctionName),
        typeof(string),
        typeof(HotkeyRecorder),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty ConfiguredChordProperty = DependencyProperty.Register(
        nameof(ConfiguredChord),
        typeof(string),
        typeof(HotkeyRecorder),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty ActiveChordProperty = DependencyProperty.Register(
        nameof(ActiveChord),
        typeof(string),
        typeof(HotkeyRecorder),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText),
        typeof(string),
        typeof(HotkeyRecorder),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty RecorderAutomationIdProperty =
        DependencyProperty.Register(
            nameof(RecorderAutomationId),
            typeof(string),
            typeof(HotkeyRecorder),
            new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift"];
    private readonly HashSet<string> _pressedModifiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalPressedModifiers = new(StringComparer.Ordinal);
    private bool _isCapturing;
    private GlobalKeyboardCaptureHook? _globalCaptureHook;
    private string? _pendingGlobalCandidate;

    public HotkeyRecorder()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    public event EventHandler<HotkeyCandidateEventArgs>? CandidateCaptured;

    public string FunctionName
    {
        get => (string)GetValue(FunctionNameProperty);
        set => SetValue(FunctionNameProperty, value);
    }

    public string ConfiguredChord
    {
        get => (string)GetValue(ConfiguredChordProperty);
        set => SetValue(ConfiguredChordProperty, value);
    }

    public string ActiveChord
    {
        get => (string)GetValue(ActiveChordProperty);
        set => SetValue(ActiveChordProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string RecorderAutomationId
    {
        get => (string)GetValue(RecorderAutomationIdProperty);
        set => SetValue(RecorderAutomationIdProperty, value);
    }

    public void RestoreConfiguredChord(string chord)
    {
        ConfiguredChord = chord;
        StatusText = ResourceText.Get("HotkeyNotRegisteredStatus");
        CancelCapture();
    }

    public void SetExternalValidation(string message)
    {
        StatusText = message;
    }

    private static void OnDisplayPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((HotkeyRecorder)dependencyObject).UpdateDisplay();
    }

    private void OnCaptureClicked(object sender, RoutedEventArgs args)
    {
        if (!_isCapturing)
        {
            if (!HotkeyCaptureCoordinator.TryBeginCapture(this))
            {
                ValidationText.Text = "Finish recording the current shortcut first.";
                return;
            }

            _isCapturing = true;
            try
            {
                _globalCaptureHook = new GlobalKeyboardCaptureHook(OnGlobalKeyTransition);
                _globalCaptureHook.Start();
            }
            catch (Win32Exception exception)
            {
                _globalCaptureHook?.Dispose();
                _globalCaptureHook = null;
                _isCapturing = false;
                HotkeyCaptureCoordinator.EndCapture(this);
                StatusText = exception.Message;
                return;
            }
        }

        _pressedModifiers.Clear();
        _globalPressedModifiers.Clear();
        _pendingGlobalCandidate = null;
        CaptureButton.Content = ResourceText.Get("HotkeyPressShortcut");
        ValidationText.Text = ResourceText.Get("HotkeyEscapeToCancel");
        CaptureButton.Focus(FocusState.Keyboard);
    }

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_isCapturing)
        {
            return;
        }

        args.Handled = true;

        if (args.Key == VirtualKey.Escape)
        {
            CancelCapture();
            return;
        }

        CaptureKeyDown(args.Key);
    }

    internal void CaptureRegisteredChord(string candidate)
    {
        if (!_isCapturing)
        {
            return;
        }

        CaptureCandidate(candidate);
    }

    internal void CancelActiveCapture() => CancelCapture();

    private void OnCaptureKeyUp(object sender, KeyRoutedEventArgs args)
    {
        string? modifier = GetModifier(args.Key);
        if (modifier is not null)
        {
            _pressedModifiers.Remove(modifier);
        }
    }

    private void OnGlobalKeyTransition(uint virtualKey, bool isDown)
    {
        if (!_isCapturing || virtualKey > byte.MaxValue)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            HandleGlobalKeyTransition((VirtualKey)virtualKey, isDown);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => HandleGlobalKeyTransition((VirtualKey)virtualKey, isDown));
    }

    private void HandleGlobalKeyTransition(VirtualKey key, bool isDown)
    {
        if (!_isCapturing)
        {
            return;
        }

        string? modifier = GetModifier(key);
        if (modifier is not null)
        {
            if (isDown)
            {
                _globalPressedModifiers.Add(modifier);
            }
            else
            {
                _globalPressedModifiers.Remove(modifier);
            }

            return;
        }

        if (isDown)
        {
            if (_globalPressedModifiers.Count >= 2)
            {
                _pendingGlobalCandidate = FormatChord(_globalPressedModifiers, GetKeyName(key));
            }

            return;
        }

        if (_pendingGlobalCandidate is { } pending)
        {
            _pendingGlobalCandidate = null;
            CaptureCandidate(pending);
        }
    }

    private void CaptureKeyDown(VirtualKey key)
    {
        string? modifier = GetModifier(key);
        if (modifier is not null)
        {
            _pressedModifiers.Add(modifier);
            ValidationText.Text = FormatChord(_pressedModifiers, null);
            return;
        }

        if (_pressedModifiers.Count < 2)
        {
            ValidationText.Text = ResourceText.Get("HotkeyNeedTwoModifiers");
            return;
        }

        CaptureCandidate(FormatChord(_pressedModifiers, GetKeyName(key)));
    }

    private void CaptureCandidate(string candidate)
    {
        if (!_isCapturing)
        {
            return;
        }

        if (IsReserved(candidate))
        {
            ValidationText.Text = ResourceText.Get("HotkeyReservedConflict");
            return;
        }

        // Keep the displayed configuration authoritative until the runtime has
        // registered and durably persisted the candidate. This prevents a
        // rejected duplicate from looking as though it was saved.
        CandidateCaptured?.Invoke(this, new HotkeyCandidateEventArgs(candidate));
        CancelCapture();
    }

    private static string? GetModifier(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Ctrl",
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
            VirtualKey.LeftWindows or VirtualKey.RightWindows => "Windows",
            _ => null,
        };
    }

    private static string GetKeyName(VirtualKey key)
    {
        string name = key.ToString();
        return name.StartsWith("Number", StringComparison.Ordinal)
            ? name[6..]
            : name;
    }

    private static string FormatChord(IEnumerable<string> modifiers, string? keyName)
    {
        IEnumerable<string> orderedModifiers = ModifierOrder.Where(modifiers.Contains);
        return keyName is null
            ? string.Join('+', orderedModifiers)
            : string.Join('+', orderedModifiers.Append(keyName));
    }

    private static bool IsReserved(string chord)
    {
        return chord is "Ctrl+Alt+K" or "Ctrl+Alt+D" or "Ctrl+Alt+Q" or "Ctrl+Alt+X";
    }

    private void CancelCapture(bool keepStatus = false)
    {
        bool wasCapturing = _isCapturing;
        _isCapturing = false;
        _pendingGlobalCandidate = null;
        _globalCaptureHook?.Dispose();
        _globalCaptureHook = null;
        if (wasCapturing)
        {
            HotkeyCaptureCoordinator.EndCapture(this);
        }

        _pressedModifiers.Clear();
        _globalPressedModifiers.Clear();
        CaptureButton.Content = ResourceText.Get("HotkeyRecordShortcut");
        if (!keepStatus)
        {
            ValidationText.Text = StatusText;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => CancelCapture(keepStatus: true);

    private void UpdateDisplay()
    {
        if (FunctionNameText is null)
        {
            return;
        }

        FunctionNameText.Text = FunctionName;
        ConfiguredChordText.Text = ConfiguredChord;
        ActiveChordText.Text = ActiveChord;
        ValidationText.Text = StatusText;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            CaptureButton,
            RecorderAutomationId);
    }
}

public sealed class HotkeyCandidateEventArgs(string chord) : EventArgs
{
    public string Chord { get; } = chord;
}
