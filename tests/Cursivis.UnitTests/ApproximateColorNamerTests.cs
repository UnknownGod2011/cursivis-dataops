using Cursivis.Application.Context;

namespace Cursivis.UnitTests;

public sealed class ApproximateColorNamerTests
{
    [Theory]
    [InlineData(0, 0, 0, "black")]
    [InlineData(255, 255, 255, "white")]
    [InlineData(31, 136, 229, "blue")]
    [InlineData(0, 142, 122, "teal")]
    [InlineData(226, 57, 53, "red")]
    public void Find_KnownColor_ReturnsStableApproximateName(
        byte red,
        byte green,
        byte blue,
        string expected)
    {
        Assert.Equal(expected, ApproximateColorNamer.Find(red, green, blue));
    }
}
