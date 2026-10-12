using GcsFileViewer.Storage;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace GcsFileViewer.Tests;

/// <summary>Progress that runs its callback on the reporting thread, so tests see reports in order.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Progress that collects reports arriving from several threads at once.</summary>
internal sealed class CollectingProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_reports)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(T value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }
    }
}

internal static class TestServices
{
    /// <summary>A real <see cref="GcsService"/> signed in with a fixed token and talking to in-memory storage.</summary>
    public static GcsService Create(FakeStorageClient client, FakeStorageClient? anonymous = null) =>
        new(new GcsEnvironment
        {
            GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("token")),
            CreateClient = _ => Task.FromResult<StorageClient>(client),
            CreateAnonymousClient = () => anonymous ?? new FakeStorageClient(),
        });
}
