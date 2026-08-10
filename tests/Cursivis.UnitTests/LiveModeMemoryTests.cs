using System.Text.Json;
using Cursivis.Application.Realtime;
using Cursivis.Infrastructure.Storage.Persistence;

namespace Cursivis.UnitTests;

public sealed class LiveModeMemoryTests
{
    [Fact]
    public async Task Store_PersistsOnlyWhenEnabledAndExplicitlyRequested()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cursivis-memory-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "memory.json");
        try
        {
            var store = new LiveModeMemoryStore(path);
            LiveModeMemorySaveResult disabled = await store.RememberExplicitAsync("Prefer concise answers");
            Assert.Equal(LiveModeMemorySaveStatus.Disabled, disabled.Status);
            Assert.Empty((await store.GetAsync()).Entries);

            await store.SetEnabledAsync(true);
            LiveModeMemorySaveResult saved = await store.RememberExplicitAsync("Prefer concise answers");
            Assert.Equal(LiveModeMemorySaveStatus.Saved, saved.Status);
            Assert.NotNull(saved.Entry);

            LiveModeMemorySaveResult duplicate = await store.RememberExplicitAsync("prefer concise answers");
            Assert.Equal(saved.Entry!.Id, duplicate.Entry!.Id);
            Assert.Single((await store.GetAsync()).Entries);

            var reloaded = new LiveModeMemoryStore(path);
            LiveModeMemorySnapshot persisted = await reloaded.GetAsync();
            Assert.True(persisted.IsEnabled);
            Assert.Equal("Prefer concise answers", Assert.Single(persisted.Entries).Text);
            Assert.True(await reloaded.DeleteAsync(saved.Entry.Id));
            Assert.Empty((await reloaded.GetAsync()).Entries);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ToolExecutor_ExposesMemoryAndTypedCapabilitiesWithoutImplicitWrites()
    {
        var memory = new FakeMemoryStore();
        var capabilities = new FakeCapabilities();
        var executor = new LiveModeToolExecutor(memory, capabilities);

        Assert.Contains(executor.Definitions, definition => definition.Name == "remember_explicitly");
        Assert.Contains(executor.Definitions, definition => definition.Name == "analyze_screen_region");
        Assert.Contains(executor.Definitions, definition => definition.Name == "start_navigation_guidance");
        Assert.Empty((await memory.GetAsync()).Entries);

        LiveModeToolExecutionResult remembered = await executor.ExecuteAsync(
            LiveModeContext.Empty,
            "remember_explicitly",
            "{\"text\":\"Use dark theme\"}");
        using JsonDocument result = JsonDocument.Parse(remembered.Json);
        Assert.True(result.RootElement.GetProperty("saved").GetBoolean());
        Assert.Equal("Use dark theme", Assert.Single((await memory.GetAsync()).Entries).Text);

        LiveModeToolExecutionResult copied = await executor.ExecuteAsync(
            LiveModeContext.Empty,
            "copy_text",
            "{\"text\":\"Useful output\"}");
        Assert.Contains("Useful output", capabilities.Copied);
        Assert.Contains("succeeded", copied.Json);

        LiveModeToolExecutionResult navigated = await executor.ExecuteAsync(
            LiveModeContext.Empty,
            "start_navigation_guidance",
            "{\"instruction\":\"Open the accessibility settings\"}");
        Assert.Contains("navigation", navigated.Json, StringComparison.OrdinalIgnoreCase);

        LiveModeToolExecutionResult analyzed = await executor.ExecuteAsync(
            LiveModeContext.Empty,
            "analyze_screen_region",
            "{\"instruction\":\"Describe the visible error message\"}");
        Assert.Equal("Describe the visible error message", capabilities.AnalyzedInstruction);
        Assert.Contains("analyzed", analyzed.Json, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(
                LiveModeContext.Empty,
                "copy_text",
                "{\"text\":\"safe\",\"extra\":true}"));
    }

    [Fact]
    public async Task ToolExecutor_GetSelectedText_PrefersFreshCaptureOverSessionStart()
    {
        var provider = new FakeContextProvider(new LiveModeContext(
            "fresh selection",
            "fp-fresh",
            "notepad",
            "Draft",
            null));
        var executor = new LiveModeToolExecutor(contextProvider: provider);
        var sessionStart = new LiveModeContext(
            "stale selection",
            "fp-stale",
            "chrome",
            "Search",
            null);

        LiveModeToolExecutionResult result = await executor.ExecuteAsync(
            sessionStart,
            "get_selected_text",
            "{}");

        using JsonDocument document = JsonDocument.Parse(result.Json);
        Assert.True(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("fresh selection", document.RootElement.GetProperty("text").GetString());
        Assert.Equal("fp-fresh", document.RootElement.GetProperty("contextFingerprint").GetString());
        Assert.Equal(1, provider.CallCount);
    }

    private sealed class FakeContextProvider(LiveModeContext context) : ILiveModeContextProvider
    {
        public int CallCount { get; private set; }

        public Task<LiveModeContext> CaptureAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(context);
        }
    }

    private sealed class FakeMemoryStore : ILiveModeMemoryStore
    {
        private readonly List<LiveModeMemoryEntry> _entries = [];

        public Task<LiveModeMemorySnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveModeMemorySnapshot(true, _entries.ToArray()));

        public Task<LiveModeMemorySnapshot> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveModeMemorySnapshot(enabled, _entries.ToArray()));

        public Task<LiveModeMemorySaveResult> RememberExplicitAsync(string text, CancellationToken cancellationToken = default)
        {
            var entry = new LiveModeMemoryEntry(Guid.NewGuid(), text, DateTimeOffset.UtcNow);
            _entries.Add(entry);
            return Task.FromResult(new LiveModeMemorySaveResult(
                LiveModeMemorySaveStatus.Saved,
                entry,
                "saved"));
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.RemoveAll(entry => entry.Id == id) > 0);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCapabilities : ILiveModeCapabilityExecutor
    {
        public List<string> Copied { get; } = [];

        public string? AnalyzedInstruction { get; private set; }

        public Task<LiveModeCapabilityResult> CopyAsync(string text, CancellationToken cancellationToken = default)
        {
            Copied.Add(text);
            return Task.FromResult(new LiveModeCapabilityResult(true, "copied"));
        }

        public Task<LiveModeCapabilityResult> InsertAsync(LiveModeContext context, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveModeCapabilityResult(true, "inserted"));

        public Task<LiveModeCapabilityResult> AnalyzeScreenAsync(string instruction, CancellationToken cancellationToken = default)
        {
            AnalyzedInstruction = instruction;
            return Task.FromResult(new LiveModeCapabilityResult(true, "analyzed", "result"));
        }

        public Task<LiveModeCapabilityResult> TakeBrowserActionAsync(string instruction, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveModeCapabilityResult(true, "completed"));

        public Task<LiveModeCapabilityResult> NavigateAsync(string instruction, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveModeCapabilityResult(true, "navigation completed"));
    }
}
