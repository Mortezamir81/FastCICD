using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastCICD.Tests;

public class UploadAndRollbackTests
{
	private static string Read(string root, string relative)
	{
		var path = Path.Combine(root, relative);
		return File.Exists(path) ? File.ReadAllText(path) : "<missing>";
	}

	private static void Write(string root, string relative, string content)
	{
		var path = Path.Combine(root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	[Fact]
	public async Task A_multi_chunk_upload_is_extracted_on_complete()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var random = new byte[300_000];
		new Random(7).NextBytes(random);
		var content = Convert.ToBase64String(random);

		var staged = await UploadHelper.StageAsync(client, "T", new() { ["data/file.txt"] = content, ["root.txt"] = "root" }, chunkSize: 100_000);
		Assert.True(staged.Zip.Length > 100_000, "the test needs more than one chunk");
		var complete = await UploadHelper.CompleteAsync(client, staged.UploadId);

		Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
		Assert.Equal(content, Read(app.SitePath("T"), @"data\file.txt"));
		Assert.Equal("root", Read(app.SitePath("T"), "root.txt"));
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/upload/sessions/{staged.UploadId}")).StatusCode);
	}

	[Fact]
	public async Task Session_status_lists_the_chunks_that_arrived()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = new string('a', 3000) }, chunkSize: 1000, sendChunks: false);
		var firstPart = staged.Zip.Take(1000).ToArray();
		(await UploadHelper.SendChunkAsync(client, staged.UploadId, 0, firstPart, ManualRequest.Sha256(firstPart))).EnsureSuccessStatusCode();

		var status = await client.GetFromJsonAsync<JsonElement>($"api/upload/sessions/{staged.UploadId}");

		Assert.Equal(new[] { 0 }, status.GetProperty("uploadedChunks").EnumerateArray().Select(e => e.GetInt32()).ToArray());
	}

	[Fact]
	public async Task Completing_an_incomplete_upload_is_refused()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "a" }, sendChunks: false);

		var complete = await UploadHelper.CompleteAsync(client, staged.UploadId);

		Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
		Assert.False(File.Exists(Path.Combine(app.SitePath("T"), "a.txt")));
	}

	[Fact]
	public async Task A_corrupted_chunk_is_rejected_and_not_counted()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = new string('a', 3000) }, chunkSize: 1000, sendChunks: false);
		var part = staged.Zip.Take(1000).ToArray();
		var corrupted = part.Select(b => (byte)(b ^ 1)).ToArray();

		var response = await UploadHelper.SendChunkAsync(client, staged.UploadId, 0, corrupted, ManualRequest.Sha256(part));
		var status = await client.GetFromJsonAsync<JsonElement>($"api/upload/sessions/{staged.UploadId}");

		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		Assert.Empty(status.GetProperty("uploadedChunks").EnumerateArray());
	}

	[Fact]
	public async Task A_chunk_with_the_wrong_length_is_rejected()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = new string('a', 3000) }, chunkSize: 1000, sendChunks: false);
		var shortPart = staged.Zip.Take(10).ToArray();

		var response = await UploadHelper.SendChunkAsync(client, staged.UploadId, 0, shortPart, ManualRequest.Sha256(shortPart));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task A_chunk_index_outside_the_range_is_rejected()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "a" }, sendChunks: false);

		var response = await UploadHelper.SendChunkAsync(client, staged.UploadId, 99, [1], ManualRequest.Sha256([1]));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task A_session_whose_final_hash_does_not_match_is_discarded()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "a" }, declaredHash: new string('a', 64));

		var complete = await UploadHelper.CompleteAsync(client, staged.UploadId);
		var lookup = await client.GetAsync($"api/upload/sessions/{staged.UploadId}");

		Assert.Equal(HttpStatusCode.UnprocessableEntity, complete.StatusCode);
		// Regression: a corrupt session used to be kept, so resuming hit the same error forever.
		Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
		Assert.False(File.Exists(Path.Combine(app.SitePath("T"), "a.txt")));
	}

	[Fact]
	public async Task Too_many_pending_sessions_are_refused()
	{
		using var app = new TestApp(extraSettings: new() { ["MaxActiveUploadSessions"] = "1" });
		var client = app.SignedClient();

		var first = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "a" }, sendChunks: false);
		var zip = UploadHelper.BuildZip(new() { ["b.txt"] = "b" });
		var second = await client.PostAsJsonAsync("api/upload/sessions", new
		{
			ProjectName = "T", Version = "1", EnableBackup = false, MirrorServerToLocal = false,
			IgnoredFiles = Array.Empty<string>(), SynchronizedFiles = Array.Empty<string>(), SyncManifestId = (string?)null,
			TotalBytes = (long)zip.Length, ChunkSize = 1 << 20, FileHash = ManualRequest.Sha256(zip)
		});

		Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
		Assert.NotNull(first.UploadId);
	}

	[Theory]
	[InlineData(0, 1 << 20, 64)]
	[InlineData(100, 0, 64)]
	[InlineData(100, 1 << 20, 10)]
	public async Task Invalid_session_requests_are_refused(long totalBytes, int chunkSize, int hashLength)
	{
		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/upload/sessions", new
		{
			ProjectName = "T", Version = "1", EnableBackup = false, MirrorServerToLocal = false,
			IgnoredFiles = Array.Empty<string>(), SynchronizedFiles = Array.Empty<string>(), SyncManifestId = (string?)null,
			TotalBytes = totalBytes, ChunkSize = chunkSize, FileHash = new string('a', hashLength)
		});
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task A_zip_entry_that_escapes_the_site_folder_is_refused_before_anything_changes()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		Write(app.SitePath("T"), "a.txt", "original");

		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "changed", ["../escaped.txt"] = "evil" });
		var complete = await UploadHelper.CompleteAsync(client, staged.UploadId);

		Assert.False(complete.IsSuccessStatusCode);
		Assert.Equal("original", Read(app.SitePath("T"), "a.txt"));
		Assert.False(File.Exists(Path.Combine(app.Root, "sites", "escaped.txt")));
	}

	[Fact]
	public async Task A_deployment_that_fails_halfway_restores_every_file_and_can_be_retried()
	{
		using var app = new TestApp();
		var site = app.SitePath("T");
		Write(site, "a.txt", "orig a");
		Write(site, "b.txt", "orig b");
		var client = app.SignedClient();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "NEW a", [@"newdir/c.txt"] = "NEW c", ["b.txt"] = "NEW b" });

		HttpResponseMessage failed;
		using (new FileStream(Path.Combine(site, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
			failed = await UploadHelper.CompleteAsync(client, staged.UploadId);

		Assert.False(failed.IsSuccessStatusCode);
		Assert.Contains("restored", await failed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
		Assert.Equal("orig a", Read(site, "a.txt"));
		Assert.Equal("orig b", Read(site, "b.txt"));
		Assert.False(Directory.Exists(Path.Combine(site, "newdir")));

		var retry = await UploadHelper.CompleteAsync(client, staged.UploadId);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		Assert.Equal("NEW a", Read(site, "a.txt"));
		Assert.Equal("NEW b", Read(site, "b.txt"));
		Assert.Equal("NEW c", Read(site, @"newdir\c.txt"));
	}

	[Fact]
	public async Task A_mirror_deployment_that_fails_while_deleting_restores_the_deleted_files()
	{
		using var app = new TestApp();
		var site = app.SitePath("T");
		Write(site, "a.txt", "orig a");
		foreach (var name in new[] { "x1.txt", "x2.txt", "x3.txt", @"sub\x4.txt" })
			Write(site, name, name);
		var client = app.SignedClient();
		var compare = await client.PostAsJsonAsync("api/compare",
			new { ProjectName = "T", FileHashes = new Dictionary<string, string> { ["a.txt"] = ManualRequest.Sha256(Encoding.UTF8.GetBytes("NEW a")) }, IgnoredFiles = Array.Empty<string>(), MirrorServerToLocal = true });
		var manifest = (await compare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("syncManifestId").GetString();
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["a.txt"] = "NEW a" }, mirror: true, syncManifestId: manifest);

		HttpResponseMessage failed;
		using (new FileStream(Path.Combine(site, "x3.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
			failed = await UploadHelper.CompleteAsync(client, staged.UploadId);

		Assert.False(failed.IsSuccessStatusCode);
		Assert.Equal("orig a", Read(site, "a.txt"));
		foreach (var name in new[] { "x1.txt", "x2.txt", "x3.txt", @"sub\x4.txt" })
			Assert.Equal(name, Read(site, name));

		var retry = await UploadHelper.CompleteAsync(client, staged.UploadId);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		Assert.Equal("NEW a", Read(site, "a.txt"));
		Assert.False(File.Exists(Path.Combine(site, "x1.txt")));
	}

	[Fact]
	public async Task Backups_are_created_and_the_version_is_recorded_when_rollback_is_enabled()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		Write(app.SitePath("T"), "old.txt", "old");
		var zip = UploadHelper.BuildZip(new() { ["new.txt"] = "new" });
		var created = await client.PostAsJsonAsync("api/upload/sessions", new
		{
			ProjectName = "T", Version = "v1.2.3", EnableBackup = true, MirrorServerToLocal = false,
			IgnoredFiles = Array.Empty<string>(), SynchronizedFiles = Array.Empty<string>(), SyncManifestId = (string?)null,
			TotalBytes = (long)zip.Length, ChunkSize = 1 << 20, FileHash = ManualRequest.Sha256(zip)
		});
		var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
		(await UploadHelper.SendChunkAsync(client, id, 0, zip, ManualRequest.Sha256(zip))).EnsureSuccessStatusCode();

		Assert.Equal(HttpStatusCode.OK, (await UploadHelper.CompleteAsync(client, id)).StatusCode);

		var backups = await client.GetFromJsonAsync<string[]>("api/backups?projectName=T");
		// The stored metadata keeps its original casing; clients read it case-insensitively.
		var version = await client.GetFromJsonAsync<VersionInfo>("api/version?projectName=T");
		Assert.Single(backups!);
		Assert.Equal("v1.2.3", version!.Version);
	}

	[Fact]
	public async Task Rolling_back_restores_the_backup_and_removes_files_added_since()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		Directory.CreateDirectory(app.BackupPath("T"));
		var backupZip = Path.Combine(app.BackupPath("T"), "backup_20260101_000000.zip");
		File.WriteAllBytes(backupZip, UploadHelper.BuildZip(new() { ["restored.txt"] = "from backup" }));
		Write(app.SitePath("T"), "added_later.txt", "should disappear");

		var response = await client.PostAsJsonAsync("api/rollback", new { ProjectName = "T", BackupFileName = "backup_20260101_000000.zip" });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("from backup", Read(app.SitePath("T"), "restored.txt"));
		Assert.False(File.Exists(Path.Combine(app.SitePath("T"), "added_later.txt")));
	}

	[Theory]
	[InlineData(@"..\..\evil.zip")]
	[InlineData(@"..\evil.zip")]
	[InlineData(@"C:\Windows\evil.zip")]
	[InlineData("sub/evil.zip")]
	[InlineData("not-a-zip.txt")]
	[InlineData("")]
	public async Task Rollback_rejects_names_that_are_not_a_plain_zip_file(string backupFileName)
	{
		using var app = new TestApp();
		Write(app.SitePath("T"), "keep.txt", "must survive");

		var response = await app.SignedClient().PostAsJsonAsync("api/rollback", new { ProjectName = "T", BackupFileName = backupFileName });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("must survive", Read(app.SitePath("T"), "keep.txt"));
	}

	[Fact]
	public async Task Rollback_of_a_missing_backup_changes_nothing()
	{
		using var app = new TestApp();
		Write(app.SitePath("T"), "keep.txt", "must survive");

		var response = await app.SignedClient().PostAsJsonAsync("api/rollback", new { ProjectName = "T", BackupFileName = "nope.zip" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("must survive", Read(app.SitePath("T"), "keep.txt"));
	}

	private sealed record VersionInfo(string Version, string DeployDate);
}
