using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Controls;

public sealed partial class SecureKeyInput : UserControl
{
    public static readonly DependencyProperty IsSecureStorageAvailableProperty =
        DependencyProperty.Register(
            nameof(IsSecureStorageAvailable),
            typeof(bool),
            typeof(SecureKeyInput),
            new PropertyMetadata(false, OnAvailabilityChanged));

    public static readonly DependencyProperty HasSavedKeyProperty = DependencyProperty.Register(
        nameof(HasSavedKey),
        typeof(bool),
        typeof(SecureKeyInput),
        new PropertyMetadata(false, OnAvailabilityChanged));

    public SecureKeyInput()
    {
        InitializeComponent();
        UpdateAvailability();
    }

    public event EventHandler? SaveRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler? TestRequested;

    public bool IsSecureStorageAvailable
    {
        get => (bool)GetValue(IsSecureStorageAvailableProperty);
        set => SetValue(IsSecureStorageAvailableProperty, value);
    }

    public bool HasSavedKey
    {
        get => (bool)GetValue(HasSavedKeyProperty);
        set => SetValue(HasSavedKeyProperty, value);
    }

    public string TakeReplacementKey()
    {
        string replacementKey = ReplacementKeyInput.Password;
        ReplacementKeyInput.Password = string.Empty;
        return replacementKey;
    }

    public void ClearReplacementKey()
    {
        ReplacementKeyInput.Password = string.Empty;
    }

    private static void OnAvailabilityChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((SecureKeyInput)dependencyObject).UpdateAvailability();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        UpdateAvailability();
    }

    private void OnSaveKeyClicked(object sender, RoutedEventArgs args)
    {
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeleteKeyClicked(object sender, RoutedEventArgs args)
    {
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTestKeyClicked(object sender, RoutedEventArgs args)
    {
        TestRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAvailability()
    {
        if (SaveKeyButton is null)
        {
            return;
        }

        SaveKeyButton.IsEnabled = IsSecureStorageAvailable
            && ReplacementKeyInput.Password.Length >= 20;
        DeleteKeyButton.IsEnabled = IsSecureStorageAvailable && HasSavedKey;
        TestKeyButton.IsEnabled = IsSecureStorageAvailable && HasSavedKey;
        StorageStatus.IsOpen = !IsSecureStorageAvailable;
    }
}
