using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Controls;

public sealed partial class SettingsCard : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(string),
        typeof(SettingsCard),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingsCard),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph),
        typeof(string),
        typeof(SettingsCard),
        new PropertyMetadata("\uE946", OnDisplayPropertyChanged));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body),
        typeof(object),
        typeof(SettingsCard),
        new PropertyMetadata(null, OnDisplayPropertyChanged));

    public SettingsCard()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    private static void OnDisplayPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((SettingsCard)dependencyObject).UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (HeaderText is null)
        {
            return;
        }

        HeaderText.Text = Header;
        DescriptionText.Text = Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CardIcon.Glyph = IconGlyph;
        BodyPresenter.Content = Body;
    }
}
