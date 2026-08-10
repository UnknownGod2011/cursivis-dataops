using Cursivis.Domain.QuickTasks;
using Cursivis.Infrastructure.Storage.Settings;

namespace Cursivis.IntegrationTests.Storage;

public sealed class QuickTaskPersistenceTests
{
    [Fact]
    public async Task Store_RoundTripsApprovedTypedDefinitionAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cursivis-quick-task-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "quick-task.json");
        try
        {
            VersionedJsonSettingsStore<QuickTaskDefinition> store = CreateStore(path);
            var definition = new QuickTaskDefinition(
                new QuickTaskId("professional-rewrite"),
                "Professional Rewrite",
                "Rewrite the explicit user-provided text professionally while preserving every fact, name, link, and constraint.",
                QuickTaskContextType.Text,
                QuickTaskOutputMode.ReplacementText,
                mayProposeAction: false,
                isExplicitlyApproved: true);

            await store.SaveAsync(definition);
            SettingsLoadResult<QuickTaskDefinition> loaded = await CreateStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(definition.Id, loaded.Value.Id);
            Assert.Equal(definition.FinalizedInstruction, loaded.Value.FinalizedInstruction);
            Assert.True(loaded.Value.IsExplicitlyApproved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static VersionedJsonSettingsStore<QuickTaskDefinition> CreateStore(string path) => new(
        new VersionedJsonSettingsStoreOptions(path, QuickTaskDefinition.CurrentSchemaVersion),
        new QuickTaskJsonSettingsCodec(static definition => definition.IsExplicitlyApproved));
}
