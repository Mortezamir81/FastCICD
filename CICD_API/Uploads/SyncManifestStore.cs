using System.Collections.Concurrent;

namespace CICD_API.Uploads;

public sealed record SyncManifest(string ProjectName, List<string> IgnoredFiles, List<string> SynchronizedFiles, DateTime ExpiresUtc);

public static class SyncManifestStore
{
	private static readonly ConcurrentDictionary<string, SyncManifest> Manifests = new();

	public static string Create(string projectName, IEnumerable<string> ignoredFiles, IEnumerable<string> synchronizedFiles)
	{
		var id = Guid.NewGuid().ToString("N");
		Manifests[id] = new SyncManifest(projectName, ignoredFiles.ToList(), synchronizedFiles.ToList(), DateTime.UtcNow.AddMinutes(30));
		return id;
	}

	public static SyncManifest? Take(string id, string projectName)
	{
		if (!Manifests.TryRemove(id, out var manifest) || manifest.ExpiresUtc < DateTime.UtcNow ||
			!string.Equals(manifest.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
			return null;
		return manifest;
	}
}
