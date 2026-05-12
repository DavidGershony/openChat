using Scramble.Core.Services;

namespace Scramble.MobileAndroid.Services;

/// <summary>
/// Pass-through ISecureStorage used by the Avalonia Android head while it remains
/// in "is this viable?" status. The native Scramble.Android app is currently the
/// canonical Android target and owns the real Android Keystore-backed implementation
/// (see Scramble.Android/Services/AndroidSecureStorage.cs).
///
/// Strategic context: the Avalonia head is a *candidate replacement* for the native
/// app — if it proves capable of full feature parity (Keystore, QR scanner, audio,
/// notifications, foreground service) the native head can be retired. Until that
/// decision point, this stub deliberately persists nothing so we don't drift the two
/// heads in incompatible directions. When a real Android run-through requires session
/// persistence, replace this with a Keystore-backed implementation that lives here
/// (or in a new shared project), NOT a reference back to Scramble.Android.
/// </summary>
public sealed class PassThroughSecureStorage : ISecureStorage
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}
