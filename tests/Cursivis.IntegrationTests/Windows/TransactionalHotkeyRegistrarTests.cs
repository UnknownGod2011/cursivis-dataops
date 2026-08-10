using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.IntegrationTests.Windows;

public sealed class TransactionalHotkeyRegistrarTests
{
    private static readonly HotkeyChord OriginalChord = new(
        HotkeyModifiers.Control | HotkeyModifiers.Alt,
        0x4F);

    private static readonly HotkeyChord ProposedChord = new(
        HotkeyModifiers.Control | HotkeyModifiers.Alt,
        0x49);

    [Fact]
    public async Task Update_WhenPersistenceFails_ReleasesProposedAndKeepsOriginalActive()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events);
        var persister = new FakeHotkeyStatePersister(events) { FailNext = true };
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);
        var initial = await registrar.RegisterInitialAsync("context-trigger", OriginalChord);
        Assert.Equal(HotkeyUpdateStatus.Success, initial.Status);
        events.Clear();

        var result = await registrar.UpdateAsync("context-trigger", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.PersistenceFailed, result.Status);
        Assert.True(result.RollbackCompleted);
        Assert.Equal(OriginalChord, result.ActiveChord);
        Assert.True(registrar.TryGetActive("context-trigger", out var active));
        Assert.Equal(OriginalChord, active!.Chord);
        Assert.Single(native.Registrations);
        Assert.Contains(OriginalChord, native.Registrations.Values);
        Assert.Equal(
            ["register:16385", "persist:context-trigger", "unregister:16385"],
            events);
    }

    [Fact]
    public async Task Update_Success_RegistersThenPersistsBeforeReleasingOriginal()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events);
        var persister = new FakeHotkeyStatePersister(events);
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);
        _ = await registrar.RegisterInitialAsync("context-trigger", OriginalChord);
        events.Clear();

        var result = await registrar.UpdateAsync("context-trigger", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.Success, result.Status);
        Assert.Equal(ProposedChord, result.ActiveChord);
        Assert.Single(native.Registrations);
        Assert.Contains(ProposedChord, native.Registrations.Values);
        Assert.Equal(ProposedChord, persister.Persisted["context-trigger"]);
        Assert.Equal(
            ["register:16385", "persist:context-trigger", "unregister:16384"],
            events);
    }

    [Fact]
    public async Task Update_WhenStartupRegistrationWasUnavailable_RegistersAndPersistsTheReplacement()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events);
        var persister = new FakeHotkeyStatePersister(events);
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);

        var result = await registrar.UpdateAsync("open-settings", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.Success, result.Status);
        Assert.Equal(ProposedChord, result.ActiveChord);
        Assert.True(registrar.TryGetActive("open-settings", out ActiveHotkeyRegistration? active));
        Assert.Equal(ProposedChord, active!.Chord);
        Assert.Equal(ProposedChord, persister.Persisted["open-settings"]);
        Assert.Equal(["register:16384", "persist:open-settings"], events);
    }

    [Fact]
    public async Task RegisterInitial_ContextTriggerDefault_RegistersWithoutPersistingAnOverride()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events);
        var persister = new FakeHotkeyStatePersister(events);
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);

        var result = await registrar.RegisterInitialAsync("context-trigger", OriginalChord);

        Assert.Equal(HotkeyUpdateStatus.Success, result.Status);
        Assert.Equal(OriginalChord, result.ActiveChord);
        Assert.True(registrar.TryGetActive("context-trigger", out ActiveHotkeyRegistration? active));
        Assert.Equal(OriginalChord, active!.Chord);
        Assert.Empty(persister.Persisted);
    }

    [Fact]
    public async Task Update_WhenInactiveCandidateConflicts_DoesNotPersistOrCreateAnActiveRegistration()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events) { ConflictChord = ProposedChord };
        var persister = new FakeHotkeyStatePersister(events);
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);

        var result = await registrar.UpdateAsync("open-settings", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.Conflict, result.Status);
        Assert.False(registrar.TryGetActive("open-settings", out _));
        Assert.Empty(persister.Persisted);
        Assert.Equal(["register-conflict:16384"], events);
    }

    [Fact]
    public async Task Update_WhenInactiveCandidateCannotPersist_ReleasesItWithoutCreatingAnActiveRegistration()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events);
        var persister = new FakeHotkeyStatePersister(events) { FailNext = true };
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);

        var result = await registrar.UpdateAsync("open-settings", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.PersistenceFailed, result.Status);
        Assert.True(result.RollbackCompleted);
        Assert.False(registrar.TryGetActive("open-settings", out _));
        Assert.Empty(native.Registrations);
        Assert.Equal(["register:16384", "persist:open-settings", "unregister:16384"], events);
    }

    [Fact]
    public async Task Update_WhenNewChordConflicts_DoesNotPersistOrReleaseOriginal()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events) { ConflictChord = ProposedChord };
        var persister = new FakeHotkeyStatePersister(events);
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);
        _ = await registrar.RegisterInitialAsync("context-trigger", OriginalChord);
        events.Clear();

        var result = await registrar.UpdateAsync("context-trigger", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.Conflict, result.Status);
        Assert.Equal(OriginalChord, result.ActiveChord);
        Assert.Empty(persister.Persisted);
        Assert.Single(native.Registrations);
        Assert.Contains(OriginalChord, native.Registrations.Values);
        Assert.Equal(["register-conflict:16385"], events);
    }

    [Fact]
    public async Task Update_WhenOriginalCannotBeReleased_RestoresPersistenceAndRemovesProposed()
    {
        var events = new List<string>();
        var native = new FakeNativeHotkeyApi(events);
        var persister = new FakeHotkeyStatePersister(events);
        await using var registrar = new TransactionalHotkeyRegistrar(
            (nint)1,
            native,
            persister);
        _ = await registrar.RegisterInitialAsync("context-trigger", OriginalChord);
        native.UnregisterFailures.Add(0x4000);
        events.Clear();

        var result = await registrar.UpdateAsync("context-trigger", ProposedChord);

        Assert.Equal(HotkeyUpdateStatus.NativeReleaseFailed, result.Status);
        Assert.True(result.RollbackCompleted);
        Assert.Equal(OriginalChord, result.ActiveChord);
        Assert.Equal(OriginalChord, persister.Persisted["context-trigger"]);
        Assert.Single(native.Registrations);
        Assert.Contains(OriginalChord, native.Registrations.Values);
        Assert.Equal(
            [
                "register:16385",
                "persist:context-trigger",
                "unregister-failed:16384",
                "unregister:16385",
                "persist:context-trigger",
            ],
            events);
    }

    [Theory]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x10)]
    public void Chord_RejectsModifierOnlyVirtualKeys(uint virtualKey)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new HotkeyChord(HotkeyModifiers.Control, virtualKey));
        Assert.Contains("ModifierOnly", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Y", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x59u)]
    [InlineData("Ctrl+Shift+F12", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7Bu)]
    [InlineData("Ctrl+Alt+Escape", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x1Bu)]
    public void ChordParser_ValidCanonicalChord_RoundTrips(
        string value,
        HotkeyModifiers modifiers,
        uint virtualKey)
    {
        Assert.True(HotkeyChordParser.TryParse(value, out HotkeyChord chord));
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(virtualKey, chord.VirtualKey);
    }

    [Theory]
    [InlineData("Y")]
    [InlineData("Ctrl+Foo+Y")]
    [InlineData("Ctrl+Alt+Delete")]
    public void ChordParser_InvalidChord_IsRejected(string value)
    {
        Assert.False(HotkeyChordParser.TryParse(value, out _));
    }

    private sealed class FakeHotkeyStatePersister(List<string> events) : IHotkeyStatePersister
    {
        public bool FailNext { get; set; }

        public Dictionary<string, HotkeyChord> Persisted { get; } = new(StringComparer.Ordinal);

        public Task PersistAsync(
            string commandName,
            HotkeyChord chord,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"persist:{commandName}");
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Injected persistence failure.");
            }

            Persisted[commandName] = chord;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNativeHotkeyApi(List<string> events) : INativeHotkeyApi
    {
        public Dictionary<int, HotkeyChord> Registrations { get; } = [];

        public HashSet<int> UnregisterFailures { get; } = [];

        public HotkeyChord? ConflictChord { get; set; }

        public NativeHotkeyOperationResult Register(
            nint windowHandle,
            int registrationId,
            HotkeyChord chord)
        {
            Assert.NotEqual(nint.Zero, windowHandle);
            if (ConflictChord == chord || Registrations.Values.Contains(chord))
            {
                events.Add($"register-conflict:{registrationId}");
                return new NativeHotkeyOperationResult(
                    false,
                    NativeHotkeyFailure.Conflict,
                    1409);
            }

            Registrations.Add(registrationId, chord);
            events.Add($"register:{registrationId}");
            return NativeHotkeyOperationResult.Success;
        }

        public NativeHotkeyOperationResult RegisterUnmodified(
            nint windowHandle,
            int registrationId,
            uint virtualKey)
        {
            Assert.NotEqual(nint.Zero, windowHandle);
            throw new NotSupportedException("This fake covers persisted modified chords only.");
        }

        public NativeHotkeyOperationResult Unregister(
            nint windowHandle,
            int registrationId)
        {
            Assert.NotEqual(nint.Zero, windowHandle);
            if (UnregisterFailures.Contains(registrationId))
            {
                events.Add($"unregister-failed:{registrationId}");
                return new NativeHotkeyOperationResult(
                    false,
                    NativeHotkeyFailure.OperatingSystemError,
                    5);
            }

            if (!Registrations.Remove(registrationId))
            {
                return new NativeHotkeyOperationResult(
                    false,
                    NativeHotkeyFailure.NotRegistered,
                    1419);
            }

            events.Add($"unregister:{registrationId}");
            return NativeHotkeyOperationResult.Success;
        }
    }
}
