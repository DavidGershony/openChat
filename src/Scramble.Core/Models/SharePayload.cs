namespace Scramble.Core.Models;

/// <summary>
/// Platform-agnostic representation of content received via Android share intent.
/// Constructed in the Android layer from intent extras, consumed by Core/Presentation logic.
/// </summary>
public class SharePayload
{
    /// <summary>Shared text (from Intent.ExtraText). Null when sharing media/files.</summary>
    public string? Text { get; init; }

    /// <summary>Content URI string(s) for shared media/files. Empty for text-only shares.</summary>
    public IReadOnlyList<string> Uris { get; init; } = Array.Empty<string>();

    /// <summary>MIME type from the share intent (e.g. "text/plain", "image/jpeg", "*/*").</summary>
    public string? MimeType { get; init; }

    /// <summary>True when the payload contains only text (no URIs).</summary>
    public bool IsText => Text != null && Uris.Count == 0;

    /// <summary>True when the payload contains one or more URIs (media or file).</summary>
    public bool IsMedia => Uris.Count > 0;

    /// <summary>True when multiple URIs were shared (ACTION_SEND_MULTIPLE).</summary>
    public bool IsMultiple => Uris.Count > 1;
}
