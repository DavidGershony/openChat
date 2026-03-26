namespace OpenChat.Core.Services;

/// <summary>
/// Uploads encrypted media blobs to Blossom servers (BUD-02).
/// </summary>
public interface IMediaUploadService
{
    /// <summary>
    /// Uploads an encrypted blob to the configured Blossom server.
    /// Returns the URL and SHA-256 hash of the uploaded blob.
    /// </summary>
    Task<BlobUploadResult> UploadAsync(byte[] encryptedData, string? privateKeyHex, string? contentType = null, CancellationToken ct = default);

    /// <summary>
    /// The currently configured Blossom server URL.
    /// </summary>
    string BlossomServerUrl { get; set; }

    /// <summary>
    /// Sets the external signer for NIP-98 auth when the user has no local private key (NIP-46).
    /// </summary>
    void SetExternalSigner(IExternalSigner? signer);
}

public class BlobUploadResult
{
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}
