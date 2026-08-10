using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Controls;

public sealed partial class SaveStateBar : UserControl
{
    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(
        nameof(StateText),
        typeof(string),
        typeof(SaveStateBar),
        new PropertyMetadata(string.Empty, OnStateChanged));

    public static readonly DependencyProperty IsDirtyProperty = DependencyProperty.Register(
        nameof(IsDirty),
        typeof(bool),
        typeof(SaveStateBar),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty CanSaveProperty = DependencyProperty.Register(
        nameof(CanSave),
        typeof(bool),
        typeof(SaveStateBar),
        new PropertyMetadata(false, OnStateChanged));

    public SaveStateBar()
    {
        InitializeComponent();
        UpdateState();
    }

    public event EventHandler? SaveRequested;

    public event EventHandler? DiscardRequested;

    public string StateText
    {
        get => (string)GetValue(StateTextProperty);
        set => SetValue(StateTextProperty, value);
    }

    public bool IsDirty
    {
        get => (bool)GetValue(IsDirtyProperty);
        set => SetValue(IsDirtyProperty, value);
    }

    public bool CanSave
    {
        get => (bool)GetValue(CanSaveProperty);
        set => SetValue(CanSaveProperty, value);
    }

    private static void OnStateChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((SaveStateBar)dependencyObject).UpdateState();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs args)
    {
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDiscardClicked(object sender, RoutedEventArgs args)
    {
        DiscardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateState()
    {
        if (StateTextBlock is null)
        {
            return;
        }

        StateTextBlock.Text = StateText;
        SaveButton.IsEnabled = IsDirty && CanSave;
        DiscardButton.IsEnabled = IsDirty;
    }
}
