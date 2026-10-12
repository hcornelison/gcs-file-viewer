using GcsFileViewer.Storage;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.ResourceManager.V3;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Time.Testing;

namespace GcsFileViewer.Tests;

/// <summary>How GcsService picks credentials, lists projects and guesses the default project.</summary>
public class GcsServiceCredentialTests
{
    private readonly FakeStorageClient _client = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<ICredential> _clientCredentials = [];

    // Records the credential each storage client is created with.
    private Task<StorageClient> CreateClient(ICredential credential)
    {
        _clientCredentials.Add(credential);
        return Task.FromResult<StorageClient>(_client);
    }

    private static Task<GoogleCredential> NoDefaultCredential() =>
        Task.FromException<GoogleCredential>(new InvalidOperationException("Your default credentials were not found."));

    private static Task<string> TokenOf(ICredential credential) => credential.GetAccessTokenForRequestAsync();

    [Fact]
    public void No_credentials_are_described_before_any_are_needed() =>
        Assert.Null(new GcsService(new GcsEnvironment()).CredentialDescription);

    [Fact]
    public async Task Application_default_credentials_are_preferred()
    {
        var gcloudCalls = 0;
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("adc-token")),
            GetGcloudAccessToken = _ => Task.FromResult($"gcloud-{++gcloudCalls}"),
            CreateClient = CreateClient,
        });

        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        Assert.Equal("Using Application Default Credentials", gcs.CredentialDescription);
        Assert.Equal("adc-token", await TokenOf(Assert.Single(_clientCredentials)));
        Assert.Equal(0, gcloudCalls);
    }

    [Fact]
    public async Task The_gcloud_login_is_used_when_there_are_no_default_credentials()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = NoDefaultCredential,
            GetGcloudAccessToken = _ => Task.FromResult("gcloud-token"),
            CreateClient = CreateClient,
            Time = _time,
        });

        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        Assert.Equal("Using gcloud CLI login", gcs.CredentialDescription);
        Assert.Equal("gcloud-token", await TokenOf(Assert.Single(_clientCredentials)));
    }

    [Fact]
    public async Task One_client_keeps_working_as_gcloud_tokens_are_replaced()
    {
        var gcloudCalls = 0;
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = NoDefaultCredential,
            GetGcloudAccessToken = _ => Task.FromResult($"gcloud-{++gcloudCalls}"),
            CreateClient = CreateClient,
            Time = _time,
        });

        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);
        var credential = Assert.Single(_clientCredentials);
        Assert.Equal("gcloud-1", await TokenOf(credential));

        _time.Advance(TimeSpan.FromMinutes(46));
        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        // The same client and credential are still in use; only the token inside has changed.
        Assert.Single(_clientCredentials);
        Assert.Equal("gcloud-2", await TokenOf(credential));
    }

    [Fact]
    public async Task With_no_credentials_at_all_the_gcloud_error_is_surfaced()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = NoDefaultCredential,
            GetGcloudAccessToken = _ => throw new InvalidOperationException("The gcloud CLI is not installed."),
        });

        var error = await Assert.ThrowsAsync<GcsException>(() => gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken));

        Assert.Equal("The gcloud CLI is not installed.", error.Message);
        Assert.Equal(GcsError.NoCredentials, error.Error);
        Assert.Null(gcs.CredentialDescription);
    }

    [Fact]
    public async Task Missing_credentials_are_looked_for_again_on_the_next_request()
    {
        var installed = false;
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = NoDefaultCredential,
            GetGcloudAccessToken = _ => installed
                ? Task.FromResult("gcloud-token")
                : throw new InvalidOperationException("The gcloud CLI is not installed."),
            CreateClient = CreateClient,
        });
        await Assert.ThrowsAsync<GcsException>(() => gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken));

        installed = true;
        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        Assert.Equal("Using gcloud CLI login", gcs.CredentialDescription);
    }

    [Fact]
    public async Task A_key_file_takes_over_from_the_discovered_credentials()
    {
        string? loaded = null;
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("adc-token")),
            LoadKeyFile = path =>
            {
                loaded = path;
                return GoogleCredential.FromAccessToken("key-token");
            },
            CreateClient = CreateClient,
        });
        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        gcs.UseKeyFile("/keys/service-account.json");
        await gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        Assert.Equal("/keys/service-account.json", loaded);
        Assert.Equal("Using key file service-account.json", gcs.CredentialDescription);
        Assert.Equal(["adc-token", "key-token"], await Task.WhenAll(_clientCredentials.Select(TokenOf)));
    }

    [Fact]
    public async Task Requests_that_start_together_share_one_client()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = async () =>
            {
                await Task.Delay(20);
                return GoogleCredential.FromAccessToken("adc-token");
            },
            CreateClient = CreateClient,
        });

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => gcs.ListBucketsAsync("proj")));

        Assert.Single(_clientCredentials);
    }

    // Listing projects

    [Fact]
    public async Task ListProjects_asks_for_active_projects_and_sorts_them()
    {
        var projects = new FakeProjectsClient("zebra", "Alpha", "mango");
        ICredential? used = null;
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("adc-token")),
            CreateProjectsClient = credential =>
            {
                used = credential;
                return Task.FromResult<ProjectsClient>(projects);
            },
        });

        var listed = await gcs.ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha", "mango", "zebra"], listed);
        Assert.Equal("state:ACTIVE", projects.Query);
        Assert.Equal("adc-token", await TokenOf(used!));
    }

    [Fact]
    public async Task ListProjects_with_no_projects_is_empty()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("adc-token")),
            CreateProjectsClient = _ => Task.FromResult<ProjectsClient>(new FakeProjectsClient()),
        });

        Assert.Empty(await gcs.ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListProjects_reports_a_refusal_as_a_storage_error()
    {
        var refused = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "Caller lacks permission."));
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("adc-token")),
            CreateProjectsClient = _ => Task.FromResult<ProjectsClient>(new FakeProjectsClient { Error = refused }),
        });

        var error = await Assert.ThrowsAsync<GcsException>(() => gcs.ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Caller lacks permission.", error.Message);
    }

    [Fact]
    public async Task ListProjects_without_credentials_says_so()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = NoDefaultCredential,
            GetGcloudAccessToken = _ => throw new InvalidOperationException("The gcloud CLI is not installed."),
        });

        var error = await Assert.ThrowsAsync<GcsException>(() => gcs.ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(GcsError.NoCredentials, error.Error);
    }

    // Default project

    [Theory]
    [InlineData("GOOGLE_CLOUD_PROJECT")]
    [InlineData("CLOUDSDK_CORE_PROJECT")]
    [InlineData("GCLOUD_PROJECT")]
    public void GuessDefaultProject_reads_the_environment_before_the_gcloud_config(string variable)
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetEnvironmentVariable = name => name == variable ? "from-env" : null,
            ReadGcloudConfig = () => ["project = from-config"],
        });

        Assert.Equal("from-env", gcs.GuessDefaultProject());
    }

    [Fact]
    public void GuessDefaultProject_falls_back_to_the_gcloud_config()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetEnvironmentVariable = _ => "",
            ReadGcloudConfig = () => ["[core]", "project = from-config"],
        });

        Assert.Equal("from-config", gcs.GuessDefaultProject());
    }

    [Fact]
    public void GuessDefaultProject_is_null_when_nothing_is_configured()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetEnvironmentVariable = _ => null,
            ReadGcloudConfig = () => null,
        });

        Assert.Null(gcs.GuessDefaultProject());
    }

    // Returns the given project IDs for any search, recording the query.
    private sealed class FakeProjectsClient(params string[] projectIds) : ProjectsClient
    {
        public string? Query { get; private set; }

        public Exception? Error { get; init; }

        public override PagedAsyncEnumerable<SearchProjectsResponse, Project> SearchProjectsAsync(
            string query, string? pageToken = null, int? pageSize = null, CallSettings? callSettings = null)
        {
            Query = query;
            return new Results(projectIds.Select(id => new Project { ProjectId = id }).ToList(), Error);
        }

        private sealed class Results(List<Project> projects, Exception? error) : PagedAsyncEnumerable<SearchProjectsResponse, Project>
        {
            public override async IAsyncEnumerator<Project> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (error is not null)
                {
                    throw error;
                }

                foreach (var project in projects)
                {
                    yield return project;
                }
            }
        }
    }
}
