using OpenChat.Presentation.Services;
using QRCoder;

namespace OpenChat.Android.Services;

public class AndroidQrCodeGenerator : IQrCodeGenerator
{
    public byte[] GeneratePng(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var pngCode = new PngByteQRCode(data);
        return pngCode.GetGraphic(pixelsPerModule);
    }
}
