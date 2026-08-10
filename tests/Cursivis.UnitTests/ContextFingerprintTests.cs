using System.Security.Cryptography;
using System.Text;
using Cursivis.Domain.Context;

namespace Cursivis.UnitTests;

public sealed class ContextFingerprintTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 7, 15, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void FromText_EqualInputs_ProduceEqualStableFingerprints()
    {
        var first = CreateText("sensitive selected text");
        var second = CreateText("sensitive selected text");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.StartsWith(ContextFingerprint.Prefix, first.Fingerprint.Value, StringComparison.Ordinal);
        Assert.Equal(ContextFingerprint.Prefix.Length + 64, first.Fingerprint.Value.Length);
    }

    [Theory]
    [InlineData("different text", "window-1", 12)]
    [InlineData("sensitive selected text", "window-2", 12)]
    [InlineData("sensitive selected text", "window-1", 13)]
    public void FromText_MaterialInputChange_ChangesFingerprint(
        string text,
        string windowId,
        long navigationGeneration)
    {
        var baseline = CreateText("sensitive selected text");
        var changed = ContextSnapshot.FromText(
            ContextKind.Text,
            ContextSource.BrowserBridge,
            new TargetIdentity("browser", windowId, "https://example.com/path", navigationGeneration),
            text,
            CapturedAt,
            TimeSpan.FromMinutes(5));

        Assert.NotEqual(baseline.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Snapshot_ToString_RedactsRawTextAndWindowIdentifier()
    {
        var snapshot = CreateText("customer@example.com private phrase");

        var diagnostic = snapshot.ToString();

        Assert.DoesNotContain("customer@example.com", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private phrase", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("window-1", snapshot.Target.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FromImageDigest_UsesDigestWithoutRetainingImageBytes()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("bounded fixture image"));
        var snapshot = ContextSnapshot.FromImageDigest(
            ContextSource.RegionCapture,
            new TargetIdentity("image-editor", "window-image"),
            digest,
            CapturedAt,
            TimeSpan.FromMinutes(1));

        Assert.Equal(ContextKind.Image, snapshot.Kind);
        Assert.True(snapshot.ImageDigest.Span.SequenceEqual(digest));
        Assert.Null(snapshot.Text);
    }

    [Fact]
    public void IsExpired_ExpiresAtBoundary()
    {
        var snapshot = CreateText("fixture");

        Assert.False(snapshot.IsExpired(snapshot.ExpiresAt.AddTicks(-1)));
        Assert.True(snapshot.IsExpired(snapshot.ExpiresAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromText_RejectsEmptyOrWhitespace(string text)
    {
        Assert.Throws<ArgumentException>(() => CreateText(text));
    }

    [Fact]
    public void TargetIdentity_NormalizesOriginAndRejectsNonWebSchemes()
    {
        var target = new TargetIdentity("browser", "window", "HTTPS://EXAMPLE.COM/path?q=1", 1);
        Assert.Equal("https://example.com", target.BrowserOrigin);

        Assert.Throws<ArgumentException>(() =>
            new TargetIdentity("browser", "window", "file:///c:/private.txt", 1));
    }

    private static ContextSnapshot CreateText(string text) => ContextSnapshot.FromText(
        ContextKind.Text,
        ContextSource.BrowserBridge,
        new TargetIdentity("browser", "window-1", "https://example.com/path", 12),
        text,
        CapturedAt,
        TimeSpan.FromMinutes(5));
}
