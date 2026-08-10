using Cursivis.Infrastructure.Storage.Development;
using Cursivis.IntegrationTests.TestSupport;

namespace Cursivis.IntegrationTests.Storage;

public sealed class DevelopmentOpenAiSecretProviderTests
{
    private const string ProcessValue = "process-development-token";
    private const string FileValue = "file-development-token";

    [Fact]
    public async Task Load_ProcessEnvironmentTakesPrecedenceWithoutExposingValueInResultText()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, ".env"),
            $"OPENAI_API_KEY={FileValue}");
        var environment = new FakeEnvironmentVariableSource(ProcessValue);
        var provider = new DevelopmentOpenAiSecretProvider(
            new DevelopmentOpenAiSecretOptions(enabled: true, temporary.Path),
            environment);

        using var result = await provider.LoadAsync();

        Assert.Equal(DevelopmentSecretLoadStatus.Success, result.Status);
        Assert.Equal(DevelopmentSecretSource.ProcessEnvironment, result.Source);
        Assert.NotNull(result.Secret);
        Assert.True(result.Secret.Use(SecretMatchesProcessValue));
        Assert.DoesNotContain(ProcessValue, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FileValue, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, environment.ReadCount);
    }

    [Fact]
    public async Task Load_UsesRootDotEnvOnlyWhenExplicitlyEnabledAndProcessValueIsAbsent()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, ".env"),
            $"OPENAI_API_KEY='{FileValue}'");
        var provider = new DevelopmentOpenAiSecretProvider(
            new DevelopmentOpenAiSecretOptions(enabled: true, temporary.Path),
            new FakeEnvironmentVariableSource(null));

        using var result = await provider.LoadAsync();

        Assert.Equal(DevelopmentSecretLoadStatus.Success, result.Status);
        Assert.Equal(DevelopmentSecretSource.DotEnvFile, result.Source);
        Assert.NotNull(result.Secret);
        Assert.True(result.Secret.Use(SecretMatchesFileValue));
        Assert.Equal("[REDACTED]", result.Secret.ToString());
    }

    [Fact]
    public async Task Load_WhenDisabled_DoesNotReadProcessEnvironmentOrDotEnv()
    {
        var environment = new FakeEnvironmentVariableSource(ProcessValue);
        var provider = new DevelopmentOpenAiSecretProvider(
            new DevelopmentOpenAiSecretOptions(enabled: false, projectRoot: null),
            environment);

        using var result = await provider.LoadAsync();

        Assert.Equal(DevelopmentSecretLoadStatus.Disabled, result.Status);
        Assert.Equal(DevelopmentSecretSource.None, result.Source);
        Assert.Null(result.Secret);
        Assert.Equal(0, environment.ReadCount);
    }

    [Fact]
    public async Task Load_DuplicateOpenAiEntries_FailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, ".env"),
            $"OPENAI_API_KEY={FileValue}{Environment.NewLine}OPENAI_API_KEY={ProcessValue}");
        var provider = new DevelopmentOpenAiSecretProvider(
            new DevelopmentOpenAiSecretOptions(enabled: true, temporary.Path),
            new FakeEnvironmentVariableSource(null));

        using var result = await provider.LoadAsync();

        Assert.Equal(DevelopmentSecretLoadStatus.Malformed, result.Status);
        Assert.Null(result.Secret);
    }

    private static bool SecretMatchesProcessValue(ReadOnlySpan<char> value) =>
        value.SequenceEqual(ProcessValue.AsSpan());

    private static bool SecretMatchesFileValue(ReadOnlySpan<char> value) =>
        value.SequenceEqual(FileValue.AsSpan());

    private sealed class FakeEnvironmentVariableSource(string? value)
        : IDevelopmentEnvironmentVariableSource
    {
        public int ReadCount { get; private set; }

        public string? GetVariable(string name)
        {
            Assert.Equal(DevelopmentOpenAiSecretProvider.VariableName, name);
            ReadCount++;
            return value;
        }
    }
}
