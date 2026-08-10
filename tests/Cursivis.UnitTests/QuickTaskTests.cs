using Cursivis.Domain.Context;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.UnitTests;

public sealed class QuickTaskTests
{
    private const string ExactPromptOptimizerInstruction =
        "Rewrite the selected rough prompt into a precise, structured, implementation-ready prompt while preserving every original requirement, constraint, named entity, link, deadline, contradiction, and requested output. Remove unnecessary repetition, filler, and ambiguity only where safely possible. Do not invent missing details or silently discard or resolve requirements. If requirements genuinely conflict, preserve and clearly mark the conflict. Output only the optimized prompt unless the user explicitly requests an explanation.";

    [Fact]
    public void CreateDefault_ContainsExactlyTheApprovedPromptOptimizerDefinition()
    {
        var configuration = QuickTaskConfiguration.CreateDefault();
        var definition = configuration.Current;

        Assert.Equal(new QuickTaskId("prompt-optimizer"), definition.Id);
        Assert.Equal("Prompt Optimizer", definition.DisplayName);
        Assert.Equal(ExactPromptOptimizerInstruction, definition.FinalizedInstruction);
        Assert.Equal(QuickTaskContextType.Text, definition.SupportedContext);
        Assert.Equal(QuickTaskOutputMode.ReplacementText, definition.OutputMode);
        Assert.False(definition.MayProposeAction);
        Assert.True(definition.IsExplicitlyApproved);
    }

    [Theory]
    [InlineData("", "Valid instruction")]
    [InlineData("   ", "Valid instruction")]
    [InlineData("Valid name", "")]
    [InlineData("Valid name", "   ")]
    public void Definition_RejectsWhitespaceOrEmptyRequiredText(string name, string instruction)
    {
        Assert.Throws<ArgumentException>(() => new QuickTaskDefinition(
            new QuickTaskId("custom-task"),
            name,
            instruction,
            QuickTaskContextType.Text,
            QuickTaskOutputMode.Analysis,
            mayProposeAction: false,
            isExplicitlyApproved: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad_id")]
    [InlineData("bad/id")]
    public void TaskId_RejectsMalformedValues(string value)
    {
        Assert.Throws<ArgumentException>(() => new QuickTaskId(value));
    }

    [Fact]
    public void TaskId_NormalizesForStableFutureCollectionIdentity()
    {
        Assert.Equal("professional-rewrite", new QuickTaskId(" Professional-Rewrite ").Value);
    }

    [Fact]
    public void Supports_UsesExplicitTextAndImageCapabilities()
    {
        var definition = CreateDefinition(QuickTaskContextType.TextAndImage);

        Assert.True(definition.Supports(ContextKind.Text));
        Assert.True(definition.Supports(ContextKind.Code));
        Assert.True(definition.Supports(ContextKind.Image));
    }

    [Fact]
    public void Definition_ToString_DoesNotRevealFinalizedInstruction()
    {
        var definition = new QuickTaskDefinition(
            new QuickTaskId("sensitive-task"),
            "Sensitive Task",
            "Transform customer@example.com private instructions",
            QuickTaskContextType.Text,
            QuickTaskOutputMode.Analysis,
            mayProposeAction: false,
            isExplicitlyApproved: true);

        Assert.DoesNotContain("customer@example.com", definition.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", definition.ToString(), StringComparison.Ordinal);
    }

    private static QuickTaskDefinition CreateDefinition(QuickTaskContextType supportedContext) => new(
        new QuickTaskId("fixture-task"),
        "Fixture Task",
        "Analyze the provided fixture.",
        supportedContext,
        QuickTaskOutputMode.Analysis,
        mayProposeAction: false,
        isExplicitlyApproved: true);
}
