using System.IO.Pipes;
using System.Text.Json;
using Cursivis.Contracts.Browser;
using Cursivis.Infrastructure.BrowserBridge;

namespace Cursivis.IntegrationTests.Browser;

public sealed class BrowserBridgeServiceTests
{
    private const string ExtensionId = "abcdefghijklmnopabcdefghijklmnop";

    [Fact]
    public async Task AuthenticatedConnection_RoundTripsStrictFormRequest()
    {
        string pipeName = $"cursivis-test-{Guid.NewGuid():N}";
        await using var service = new BrowserBridgeService(
            new HashSet<string>(StringComparer.Ordinal) { ExtensionId },
            pipeName);
        await service.StartAsync();
        using var native = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        await native.ConnectAsync(5_000);

        string handshakeCorrelation = Guid.NewGuid().ToString("N");
        await NativeMessagingFraming.WriteEnvelopeAsync(
            native,
            BrowserEnvelope.Create(
                BrowserMessageTypes.Hello,
                handshakeCorrelation,
                new BrowserHello(
                    ExtensionId,
                    "1.2.3",
                    CreateNonce(),
                    DateTimeOffset.UtcNow.AddSeconds(20),
                    ["selection", "form_discovery", "safe_form_fill"])),
            CancellationToken.None);
        BrowserEnvelope welcomeEnvelope = Assert.IsType<BrowserEnvelope>(
            await NativeMessagingFraming.ReadEnvelopeAsync(native, CancellationToken.None));
        BrowserWelcome welcome = Assert.IsType<BrowserWelcome>(
            welcomeEnvelope.Payload.Deserialize<BrowserWelcome>(BrowserJson.SerializerOptions));
        Assert.Equal(BrowserMessageTypes.Welcome, welcomeEnvelope.Type);
        Assert.Equal(handshakeCorrelation, welcomeEnvelope.CorrelationId);
        Assert.True(service.Snapshot.IsConnected);

        Task<BrowserFormSnapshot> request = service.DiscoverFormAsync();
        BrowserEnvelope discover = Assert.IsType<BrowserEnvelope>(
            await NativeMessagingFraming.ReadEnvelopeAsync(native, CancellationToken.None));
        Assert.Equal(BrowserMessageTypes.DiscoverForm, discover.Type);
        Assert.Equal(welcome.SessionToken, discover.SessionToken);

        BrowserFormSnapshot form = Form();
        await NativeMessagingFraming.WriteEnvelopeAsync(
            native,
            BrowserEnvelope.Create(
                BrowserMessageTypes.Form,
                discover.CorrelationId,
                form,
                sessionToken: welcome.SessionToken),
            CancellationToken.None);

        BrowserFormSnapshot actual = await request;
        Assert.Equal(form.Tab, actual.Tab);
        Assert.Equal(form.FormId, actual.FormId);
        Assert.Equal(form.ContextFingerprint, actual.ContextFingerprint);
        Assert.Equal("target-name", Assert.Single(actual.Fields).StableTargetId);
        Assert.Equal("target-submit", Assert.Single(actual.Controls).StableTargetId);
    }

    [Fact]
    public async Task InvalidExtension_IsRejectedAndNeverBecomesConnected()
    {
        string pipeName = $"cursivis-test-{Guid.NewGuid():N}";
        await using var service = new BrowserBridgeService(
            new HashSet<string>(StringComparer.Ordinal) { ExtensionId },
            pipeName);
        await service.StartAsync();
        using var native = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        await native.ConnectAsync(5_000);

        await NativeMessagingFraming.WriteEnvelopeAsync(
            native,
            BrowserEnvelope.Create(
                BrowserMessageTypes.Hello,
                "bad-extension",
                new BrowserHello(
                    "pppppppppppppppppppppppppppppppp",
                    "1.0",
                    CreateNonce(),
                    DateTimeOffset.UtcNow.AddSeconds(20),
                    ["form_discovery"])),
            CancellationToken.None);

        BrowserEnvelope response = Assert.IsType<BrowserEnvelope>(
            await NativeMessagingFraming.ReadEnvelopeAsync(native, CancellationToken.None));
        BrowserError error = Assert.IsType<BrowserError>(
            response.Payload.Deserialize<BrowserError>(BrowserJson.SerializerOptions));
        Assert.Equal(BrowserMessageTypes.Error, response.Type);
        Assert.Equal("extension_not_allowed", error.Code);
        Assert.False(service.Snapshot.IsConnected);
    }

    private static BrowserFormSnapshot Form() => new(
        new BrowserTabIdentity(8, 2, "https://example.test", true, true),
        "form-one",
        "Example",
        [
            new BrowserFormField(
                "target-name",
                BrowserFormFieldKind.Text,
                "Name",
                null,
                true,
                false,
                string.Empty,
                []),
        ],
        [new BrowserFormControl("target-submit", BrowserFormControlKind.Submit, "Send", true)],
        "fingerprint-one");

    private static string CreateNonce()
    {
        string nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return nonce.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
