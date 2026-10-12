using GcsFileViewer.GcloudAuth;
using Google.Apis.Auth.OAuth2;

namespace GcsFileViewer.Storage;

/// <summary>
/// Chooses the credentials to sign requests with: a key file the user picked, otherwise Application Default
/// Credentials, otherwise the gcloud CLI's own login.
/// </summary>
internal sealed class CredentialProvider(GcsEnvironment environment)
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _keyFilePath;
    private ICredential? _credential;

    /// <summary>Which credentials are in use, once they have been resolved.</summary>
    public string? Description { get; private set; }

    /// <summary>Switches to a service account key file, forgetting whatever was in use.</summary>
    public void UseKeyFile(string path)
    {
        _keyFilePath = path;
        _credential = null;
    }

    /// <exception cref="GcsException">No usable credentials were found.</exception>
    public async Task<ICredential> GetAsync(CancellationToken ct = default)
    {
        // One caller at a time, so requests that start together resolve the credentials once.
        await _gate.WaitAsync(ct);
        try
        {
            return _credential ??= await ResolveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ICredential> ResolveAsync(CancellationToken ct)
    {
        if (_keyFilePath is { } path)
        {
            var fromFile = Scoped(environment.LoadKeyFile(path));
            Description = $"Using key file {Path.GetFileName(path)}";
            return fromFile;
        }

        try
        {
            var fromDefault = Scoped(await environment.GetDefaultCredential());
            Description = "Using Application Default Credentials";
            return fromDefault;
        }
        catch (InvalidOperationException)
        {
            // No default credentials on this machine; fall through to gcloud.
        }

        try
        {
            // Fetch a token now, so a missing or signed-out gcloud is reported here and not on the first request.
            var fromGcloud = new GcloudCredential(environment.GetGcloudAccessToken, environment.Time);
            await fromGcloud.GetAccessTokenForRequestAsync(cancellationToken: ct);
            Description = "Using gcloud CLI login";
            return fromGcloud;
        }
        catch (InvalidOperationException ex)
        {
            throw new GcsException(ex.Message, GcsError.NoCredentials, ex);
        }
    }

    private static GoogleCredential Scoped(GoogleCredential credential) =>
        credential.IsCreateScopedRequired ? credential.CreateScoped(CloudPlatformScope) : credential;
}
