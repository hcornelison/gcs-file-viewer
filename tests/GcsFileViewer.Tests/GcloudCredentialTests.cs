using GcsFileViewer.GcloudAuth;
using Microsoft.Extensions.Time.Testing;

namespace GcsFileViewer.Tests;

public class GcloudCredentialTests
{
    private readonly FakeTimeProvider _time = new();
    private int _fetches;

    private Task<string> NextToken(CancellationToken ct) => Task.FromResult($"token-{++_fetches}");

    [Fact]
    public async Task No_token_is_fetched_until_one_is_needed()
    {
        _ = new GcloudCredential(NextToken, _time);

        await Task.Yield();

        Assert.Equal(0, _fetches);
    }

    [Fact]
    public async Task A_token_is_reused_until_it_is_45_minutes_old_then_replaced()
    {
        var credential = new GcloudCredential(NextToken, _time);

        Assert.Equal("token-1", await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));
        _time.Advance(TimeSpan.FromMinutes(44));
        Assert.Equal("token-1", await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal("token-2", await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("token-2", await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task The_lifetime_can_be_set()
    {
        var credential = new GcloudCredential(NextToken, _time, TimeSpan.FromMinutes(5));

        await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("token-2", await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Requests_that_start_together_share_one_trip_to_gcloud()
    {
        var credential = new GcloudCredential(
            async ct =>
            {
                await Task.Delay(20, ct);
                return $"token-{Interlocked.Increment(ref _fetches)}";
            },
            _time);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => credential.GetAccessTokenForRequestAsync()));

        Assert.All(tokens, token => Assert.Equal("token-1", token));
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task A_failed_fetch_is_reported_and_tried_again_next_time()
    {
        var signedIn = false;
        var credential = new GcloudCredential(
            _ => signedIn ? Task.FromResult("token") : throw new InvalidOperationException("Not signed in."),
            _time);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Not signed in.", error.Message);

        signedIn = true;

        Assert.Equal("token", await credential.GetAccessTokenForRequestAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_request_does_not_fetch()
    {
        var credential = new GcloudCredential(NextToken, _time);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => credential.GetAccessTokenForRequestAsync(cancellationToken: cts.Token));

        Assert.Equal(0, _fetches);
    }

    [Fact]
    public async Task Requests_are_signed_with_the_current_token()
    {
        var credential = new GcloudCredential(NextToken, _time);
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://storage.googleapis.com/");
        using var later = new HttpRequestMessage(HttpMethod.Get, "https://storage.googleapis.com/");

        await credential.InterceptAsync(first, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await credential.InterceptAsync(later, CancellationToken.None);

        Assert.Equal("Bearer token-1", first.Headers.Authorization?.ToString());
        Assert.Equal("Bearer token-2", later.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task The_token_is_also_available_with_headers()
    {
        var credential = new GcloudCredential(NextToken, _time);

        var token = await credential.GetAccessTokenWithHeadersForRequestAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("token-1", token.AccessToken);
        Assert.Empty(token.Headers);
    }

    [Fact]
    public async Task It_plugs_into_a_google_http_client()
    {
        var credential = new GcloudCredential(NextToken, _time);
        var handler = new Google.Apis.Http.ConfigurableMessageHandler(new Echo());
        using var client = new Google.Apis.Http.ConfigurableHttpClient(handler);

        credential.Initialize(client);
        using var response = await client.GetAsync("https://storage.googleapis.com/", TestContext.Current.CancellationToken);

        Assert.Equal("Bearer token-1", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // Answers every request with the Authorization header it arrived with.
    private sealed class Echo : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(request.Headers.Authorization?.ToString() ?? "none"),
            });
    }
}
