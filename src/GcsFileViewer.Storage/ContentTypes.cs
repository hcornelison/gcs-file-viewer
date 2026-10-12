using MimeMapping;

namespace GcsFileViewer.Storage;

/// <summary>Content types for uploads, and how a file's type is described in the list.</summary>
internal static class ContentTypes
{
    /// <summary>Content type to store for an uploaded file, from its extension; null lets Cloud Storage use its default.</summary>
    public static string? Guess(string name)
    {
        var type = MimeUtility.GetMimeMapping(name);
        return type == MimeUtility.UnknownMimeType ? null : type;
    }

    /// <summary>A specific content type if the object has one, otherwise a description from its extension.</summary>
    public static string Describe(string name, string? contentType)
    {
        if (!string.IsNullOrEmpty(contentType) && contentType != MimeUtility.UnknownMimeType)
        {
            return contentType;
        }

        var extension = Path.GetExtension(name).TrimStart('.');
        return extension.Length > 0 ? $"{extension.ToUpperInvariant()} file" : "File";
    }
}
