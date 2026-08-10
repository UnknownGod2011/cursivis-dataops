using System;

using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        Version? version = typeof(AboutPage).Assembly.GetName().Version;
        VersionText = version?.ToString(3) ?? ResourceText.Get("VersionUnavailable");
        BuildCommitText = ResourceText.Get("BuildCommitNotEmbedded");
        InitializeComponent();
    }

    public string VersionText { get; }

    public string BuildCommitText { get; }
}
