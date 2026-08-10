using Cursivis.Application.Presentation;
using Cursivis.Domain.Actions;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Settings;

namespace Cursivis.UnitTests;

public sealed class OverlayPresentationTests
{
    [Fact]
    public void ResultPresentation_PreservesReplaceAndCopyCommands()
    {
        var result = new SmartResult(
            ContextKind.Text,
            SmartIntent.Rewrite,
            0.96,
            "A clearer result.",
            new SuggestedAction(SuggestedActionType.ReplaceSelection, "Replace the selected text"),
            RiskLevel.Low,
            ConfirmationHint.ContextDependent);

        ResultPanelPresentation presentation = ResultPanelPresentation.FromResult(result, guidedMode: false);

        Assert.Equal(ResultPanelStatus.Ready, presentation.Status);
        Assert.Equal("Rewrite", presentation.OperationLabel);
        Assert.Equal("Text", presentation.ContextLabel);
        Assert.True(presentation.CanCopy);
        Assert.True(presentation.CanReplace);
        Assert.True(presentation.CanTakeAction);
        Assert.True(presentation.CanRefine);
        Assert.True(presentation.CanInsert);
    }

    [Fact]
    public void FailurePresentation_DisablesMutatingCommands()
    {
        ResultPanelPresentation presentation = ResultPanelPresentation.Failure(
            "No selection found",
            "Select text and try again.");

        Assert.Equal(ResultPanelStatus.Error, presentation.Status);
        Assert.Equal("System status", presentation.ContextLabel);
        Assert.False(presentation.CanCopy);
        Assert.False(presentation.CanReplace);
        Assert.False(presentation.CanTakeAction);
        Assert.Equal("No selection found", presentation.NoticeTitle);
    }

    [Fact]
    public void ImagePresentation_DisablesSelectionReplacementButKeepsUsefulActions()
    {
        var result = new SmartResult(
            ContextKind.Image,
            SmartIntent.Identify,
            0.94,
            "A grounded image description.",
            new SuggestedAction(SuggestedActionType.Copy, "Copy the analysis"),
            RiskLevel.None,
            ConfirmationHint.None);

        ResultPanelPresentation presentation = ResultPanelPresentation.FromResult(result, guidedMode: false);

        Assert.False(presentation.CanReplace);
        Assert.True(presentation.CanCopy);
        Assert.True(presentation.CanInsert);
        Assert.True(presentation.CanTakeAction);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, false, false, EffectiveOverlayTheme.Light)]
    [InlineData(ApplicationTheme.Dark, false, false, EffectiveOverlayTheme.Dark)]
    [InlineData(ApplicationTheme.System, true, false, EffectiveOverlayTheme.Dark)]
    [InlineData(ApplicationTheme.System, false, false, EffectiveOverlayTheme.Light)]
    [InlineData(ApplicationTheme.Dark, true, true, EffectiveOverlayTheme.HighContrast)]
    public void ThemeResolver_UsesPreferenceSystemAndAccessibilityOverrides(
        ApplicationTheme preference,
        bool systemUsesDark,
        bool highContrast,
        EffectiveOverlayTheme expected)
    {
        Assert.Equal(expected, OverlayThemeResolver.Resolve(preference, systemUsesDark, highContrast));
    }

    [Fact]
    public void ResultPanelSizeCalculator_ShortResult_UsesCompactInitialHeight()
    {
        ResultPanelLogicalSize size = ResultPanelSizeCalculator.Calculate(
            "Short result.",
            hasNotice: false);

        Assert.Equal(ResultPanelSizeCalculator.DefaultWidth, size.Width);
        Assert.Equal(ResultPanelSizeCalculator.MinimumHeight, size.Height);
    }

    [Fact]
    public void ResultPanelSizeCalculator_LongResult_ClampsInitialHeight()
    {
        ResultPanelLogicalSize size = ResultPanelSizeCalculator.Calculate(
            new string('x', 10_000),
            hasNotice: true);

        Assert.Equal(ResultPanelSizeCalculator.MaximumInitialHeight, size.Height);
    }

    [Fact]
    public void OrbState_CoversEveryRequiredVisibleState()
    {
        string[] names = Enum.GetNames<OrbPresentationState>();

        Assert.Equal(
            [
                "Idle", "Listening", "Thinking", "Generating", "Speaking",
                "Guiding", "Executing", "Done", "Cancelled", "Error",
            ],
            names);
    }
}
