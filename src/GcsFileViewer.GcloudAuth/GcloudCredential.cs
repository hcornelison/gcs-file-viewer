using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;

namespace GcsFileViewer.GcloudAuth;

/// <summary>
/// A Google credential backed by the gcloud CLI's own login. gcloud's access tokens last an hour and can't renew
/// themselves, so this fetches a new one from gcloud whenever the current one is getting old. Clients built
/// with it keep working however long they are used.
/// </summary>
public sealed class GcloudCredential : ICredential, IHttpExecuteInterceptor
{
    /// <summary>gcloud access tokens last an hour; replace them well before that.</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(45);

    private readonly Func<CancellationToken, Task<string>> _fetchToken;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public GcloudCredential()
        : this(GcloudCli.GetAccessTokenAsync)
    {
    }

    /// <param name="fetchToken">Gets a fresh access token, as <see cref="GcloudCli.GetAccessTokenAsync(CancellationToken)"/> does.</param>
    /// <param name="time">The clock used to age tokens.</param>
    /// <param name="tokenLifetime">How long a token is used before another is fetched.</param>
    public GcloudCredential(
        Func<CancellationToken, Task<string>> fetchToken, TimeProvider? time = null, TimeSpan? tokenLifetime = null)
    {
        _fetchToken = fetchToken;
        _time = time ?? TimeProvider.System;
        _lifetime = tokenLifetime ?? DefaultTokenLifetime;
    }

    public void Initialize(ConfigurableHttpClient httpClient) => httpClient.MessageHandler.Credential = this;

    public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetAccessTokenForRequestAsync(null, cancellationToken));

    public async Task<string> GetAccessTokenForRequestAsync(string? authUri = null, CancellationToken cancellationToken = default)
    {
        // One caller at a time, so several requests starting together share a single trip to gcloud.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is null || _time.GetUtcNow() >= _expires)
            {
                _token = await _fetchToken(cancellationToken);
                _expires = _time.GetUtcNow() + _lifetime;
            }

            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccessTokenWithHeaders> GetAccessTokenWithHeadersForRequestAsync(
        string? authUri = null, CancellationToken cancellationToken = default) =>
        new(await GetAccessTokenForRequestAsync(authUri, cancellationToken), new Dictionary<string, IReadOnlyList<string>>());
}
