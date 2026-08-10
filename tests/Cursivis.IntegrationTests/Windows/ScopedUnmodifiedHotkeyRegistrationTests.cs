using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.IntegrationTests.Windows;

public sealed class ScopedUnmodifiedHotkeyRegistrationTests
{
    [Fact]
    public void EnableAndDisable_OwnsEscapeOnlyWithinVisibleLifetime()
    {
        var native = new FakeNativeHotkeyApi();
        using var registration = new ScopedUnmodifiedHotkeyRegistration(
            (nint)1,
            registrationId: 0x3FFE,
            virtualKey: 0x1B,
            native);

        Assert.True(registration.Enable());
        Assert.True(registration.IsEnabled);
        Assert.True(registration.Matches(0x3FFE));
        Assert.Equal((0x3FFE, 0x1Bu), native.Active);

        registration.Disable();

        Assert.False(registration.IsEnabled);
        Assert.False(registration.Matches(0x3FFE));
        Assert.Null(native.Active);
    }

    [Fact]
    public void Enable_WhenEscapeConflicts_LeavesRegistrationDisabled()
    {
        var native = new FakeNativeHotkeyApi { Conflict = true };
        using var registration = new ScopedUnmodifiedHotkeyRegistration(
            (nint)1,
            registrationId: 0x3FFE,
            virtualKey: 0x1B,
            native);

        Assert.False(registration.Enable());
        Assert.False(registration.IsEnabled);
        Assert.False(registration.Matches(0x3FFE));
    }

    private sealed class FakeNativeHotkeyApi : INativeHotkeyApi
    {
        public bool Conflict { get; init; }

        public (int Id, uint Key)? Active { get; private set; }

        public NativeHotkeyOperationResult Register(
            nint windowHandle,
            int registrationId,
            HotkeyChord chord) =>
            throw new NotSupportedException();

        public NativeHotkeyOperationResult RegisterUnmodified(
            nint windowHandle,
            int registrationId,
            uint virtualKey)
        {
            if (Conflict)
            {
                return new NativeHotkeyOperationResult(
                    false,
                    NativeHotkeyFailure.Conflict,
                    1409);
            }

            Active = (registrationId, virtualKey);
            return NativeHotkeyOperationResult.Success;
        }

        public NativeHotkeyOperationResult Unregister(
            nint windowHandle,
            int registrationId)
        {
            if (Active?.Id != registrationId)
            {
                return new NativeHotkeyOperationResult(
                    false,
                    NativeHotkeyFailure.NotRegistered,
                    1419);
            }

            Active = null;
            return NativeHotkeyOperationResult.Success;
        }
    }
}
