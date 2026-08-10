using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Controls;

public sealed partial class SettingsSectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SettingsSectionHeader),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingsSectionHeader),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText),
        typeof(string),
        typeof(SettingsSectionHeader),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty StatusDetailProperty = DependencyProperty.Register(
        nameof(StatusDetail),
        typeof(string),
        typeof(SettingsSectionHeader),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty StatusGlyphProperty = DependencyProperty.Register(
        nameof(StatusGlyph),
        typeof(string),
        typeof(SettingsSectionHeader),
        new PropertyMetadata("\uE946", OnDisplayPropertyChanged));

    public SettingsSectionHeader()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string StatusDetail
    {
        get => (string)GetValue(StatusDetailProperty);
        set => SetValue(StatusDetailProperty, value);
    }

    public string StatusGlyph
    {
        get => (string)GetValue(StatusGlyphProperty);
        set => SetValue(StatusGlyphProperty, value);
    }

    private static void OnDisplayPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((SettingsSectionHeader)dependencyObject).UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (TitleText is null)
        {
            return;
        }

        TitleText.Text = Title;
        DescriptionText.Text = Description;
        SectionStatus.StatusText = StatusText;
        SectionStatus.DetailText = StatusDetail;
        SectionStatus.Glyph = StatusGlyph;
    }
}
