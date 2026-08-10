using Cursivis.Application.Context;
using Cursivis.Domain.Context;

namespace Cursivis.UnitTests;

public sealed class TextContextKindClassifierTests
{
    [Theory]
    [InlineData("What caused this error?", ContextKind.Question)]
    [InlineData("From: Alex\nTo: Taylor\nCan we meet tomorrow?", ContextKind.Email)]
    [InlineData("public static int Add(int a, int b) { return a + b; }", ContextKind.Code)]
    [InlineData("Name|Owner|Status\nAPI|Sam|Ready", ContextKind.Table)]
    [InlineData("Rewrite this sentence.", ContextKind.Text)]
    public void Classify_RecognizesGuidedContextKinds(string text, ContextKind expected)
    {
        Assert.Equal(expected, TextContextKindClassifier.Classify(text));
    }
}
