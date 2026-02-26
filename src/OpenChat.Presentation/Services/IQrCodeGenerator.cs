namespace OpenChat.Presentation.Services;

/// <summary>
/// Platform-agnostic QR code generator. Returns PNG bytes.
/// </summary>
public interface IQrCodeGenerator
{
    byte[] GeneratePng(string text, int pixelsPerModule = 8);
}
