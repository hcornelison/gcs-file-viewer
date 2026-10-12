using Google.Cloud.ResourceManager.V3;

namespace GcsFileViewer.Storage;

internal static class ProjectLister
{
    /// <summary>The IDs of the active projects the client's credentials can see, sorted by name.</summary>
    public static async Task<List<string>> ListActiveAsync(ProjectsClient client, CancellationToken ct)
    {
        var projects = new List<string>();
        await foreach (var project in client.SearchProjectsAsync("state:ACTIVE").WithCancellation(ct))
        {
            projects.Add(project.ProjectId);
        }

        projects.Sort(StringComparer.OrdinalIgnoreCase);
        return projects;
    }
}
