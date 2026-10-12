using System.Net;
using Google;

namespace GcsFileViewer.Storage;

/// <summary>Why a storage request failed, for callers that want to react differently to different failures.</summary>
public enum GcsError
{
    Other,
    NotFound,
    AlreadyExists,
    PermissionDenied,
    NoCredentials,
}

/// <summary>
/// A failed storage request, with a message fit to show to the user. Everything <see cref="IGcsService"/> throws
/// for a failed request is one of these, so callers don't need to know which client library is underneath.
/// </summary>
public sealed class GcsException(string message, GcsError error = GcsError.Other, Exception? innerException = null)
    : Exception(message, innerException)
{
    public GcsError Error { get; } = error;

    internal static GcsException From(GoogleApiException exception)
    {
        var message = exception.Error?.Message is { Length: > 0 } detail ? detail : exception.Message;
        return exception.HttpStatusCode switch
        {
            // Writes that mustn't overwrite are sent with a precondition, and this is how it reports a clash.
            HttpStatusCode.PreconditionFailed =>
                new GcsException("An item with that name already exists.", GcsError.AlreadyExists, exception),
            HttpStatusCode.NotFound => new GcsException(message, GcsError.NotFound, exception),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                new GcsException(message, GcsError.PermissionDenied, exception),
            _ => new GcsException(message, GcsError.Other, exception),
        };
    }
}
