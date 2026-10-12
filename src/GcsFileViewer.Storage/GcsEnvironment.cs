using GcsFileViewer.GcloudAuth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.ResourceManager.V3;
using Google.Cloud.Storage.V1;

namespace GcsFileViewer.Storage;

/// <summary>
/// Everything <see cref="GcsService"/> reaches outside the process for: credentials, the gcloud CLI, Google's
/// clients and the clock. The defaults are the real thing; tests substitute their own.
/// </summary>
internal sealed class GcsEnvironment
{
    public Func<Task<GoogleCredential>> GetDefaultCredential { get; init; } =
        () => GoogleCredential.GetApplicationDefaultAsync();

#pragma warning disable CS0618 // The key file is one the user picked themselves.
    public Func<string, GoogleCredential> LoadKeyFile { get; init; } = path => GoogleCredential.FromFile(path);
#pragma warning restore CS0618

    public Func<CancellationToken, Task<string>> GetGcloudAccessToken { get; init; } = GcloudCli.GetAccessTokenAsync;

    public Func<ICredential, Task<StorageClient>> CreateClient { get; init; } =
        credential => new StorageClientBuilder { Credential = credential }.BuildAsync();

    public Func<StorageClient> CreateAnonymousClient { get; init; } = StorageClient.CreateUnauthenticated;

    public Func<ICredential, Task<ProjectsClient>> CreateProjectsClient { get; init; } =
        credential => new ProjectsClientBuilder { Credential = credential }.BuildAsync();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The lines of gcloud's default configuration file, or null if there isn't one.</summary>
    public Func<IEnumerable<string>?> ReadGcloudConfig { get; init; } = GcloudCli.ReadDefaultConfig;
}
