using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Controls;

public sealed partial class StatusIndicator : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(StatusIndicator),
        new PropertyMetadata("\uE946", OnDisplayPropertyChanged));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText),
        typeof(string),
        typeof(StatusIndicator),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty DetailTextProperty = DependencyProperty.Register(
        nameof(DetailText),
        typeof(string),
        typeof(StatusIndicator),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public StatusIndicator()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string DetailText
    {
        get => (string)GetValue(DetailTextProperty);
        set => SetValue(DetailTextProperty, value);
    }

    private static void OnDisplayPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((StatusIndicator)dependencyObject).UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (StatusIcon is null)
        {
            return;
        }

        StatusIcon.Glyph = Glyph;
        StatusLabel.Text = StatusText;
        StatusDetail.Text = DetailText;
        StatusDetail.Visibility = string.IsNullOrWhiteSpace(DetailText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
