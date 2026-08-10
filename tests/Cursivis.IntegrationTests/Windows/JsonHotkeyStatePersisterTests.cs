using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.IntegrationTests.Windows;

public sealed class JsonHotkeyStatePersisterTests
{
    [Fact]
    public async Task PersistAsync_ReloadsValidatedChordAndPreservesOtherCommands()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cursivis-hotkeys-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "hotkeys.json");
        try
        {
            var persister = new JsonHotkeyStatePersister(path);
            var quickTask = new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x59);
            var context = new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4F);

            await persister.PersistAsync("custom-quick-task", quickTask);
            await persister.PersistAsync("context-trigger", context);

            var reloaded = new JsonHotkeyStatePersister(path);
            Assert.True(reloaded.TryLoad("custom-quick-task", out HotkeyChord loadedQuickTask));
            Assert.True(reloaded.TryLoad("context-trigger", out HotkeyChord loadedContext));
            Assert.Equal(quickTask, loadedQuickTask);
            Assert.Equal(context, loadedContext);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
