namespace OpenChat.Core.Services;

/// <summary>
/// Platform-agnostic interface for encrypting/decrypting sensitive data at rest.
/// Implementations use OS-native secure storage (DPAPI on Windows, Android Keystore on Android).
/// </summary>
public interface ISecureStorage
{
    /// <summary>
    /// Encrypts the given data using platform-specific secure storage.
    /// The output includes a magic prefix to identify encrypted data.
    /// </summary>
    byte[] Protect(byte[] data);

    /// <summary>
    /// Decrypts the given data using platform-specific secure storage.
    /// If the data does not have the encryption magic prefix, returns it as-is (backward compat).
    /// </summary>
    byte[] Unprotect(byte[] data);
}
