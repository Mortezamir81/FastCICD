using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastCICD.Tests;

public class CompareAndMirrorTests
{
	private static string Hash(string content) => ManualRequest.Sha256(Encoding.UTF8.GetBytes(content));

	private static void Write(string root, string relative, string content)
	{
		var path = Path.Combine(root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private static async Task<JsonElement> Compare(HttpClient client, string project, Dictionary<string, string> hashes, string[]? ignored = null, bool mirror = false)
	{
		var response = await client.PostAsJsonAsync("api/compare", new { ProjectName = project, FileHashes = hashes, IgnoredFiles = ignored ?? [], MirrorServerToLocal = mirror });
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	[Fact]
	public async Task Reports_new_and_changed_files_but_not_identical_ones()
	{
		using var app = new TestApp();
		Write(app.SitePath("T"), "same.txt", "same");
		Write(app.SitePath("T"), "changed.txt", "old");

		var result = await Compare(app.SignedClient(), "T", new()
		{
			["same.txt"] = Hash("same"),
			["changed.txt"] = Hash("new"),
			[@"sub\new.txt"] = Hash("x"),
		});

		var delta = result.GetProperty("deltaFiles").EnumerateArray().Select(e => e.GetString()).Order().ToArray();
		Assert.Equal(new[] { "changed.txt", @"sub\new.txt" }, delta);
	}

	[Fact]
	public async Task Hash_comparison_ignores_letter_case()
	{
		using var app = new TestApp();
		Write(app.SitePath("T"), "a.txt", "same");

		var result = await Compare(app.SignedClient(), "T", new() { ["a.txt"] = Hash("same").ToUpperInvariant() });

		Assert.Empty(result.GetProperty("deltaFiles").EnumerateArray());
	}

	[Fact]
	public async Task A_site_folder_written_with_forward_slashes_still_works()
	{
		var temp = Path.Combine(Path.GetTempPath(), "FastCICD-Tests", Guid.NewGuid().ToString("N"), "site");
		Directory.CreateDirectory(temp);
		try
		{
			using var app = new TestApp(["F"], new() { ["AllowedDirectories:F"] = temp.Replace('\\', '/') });
			Write(temp, "a.txt", "same");

			var result = await Compare(app.SignedClient(), "F", new() { ["a.txt"] = Hash("same"), ["b.txt"] = Hash("missing") });

			// Regression: this used to skip every file and report "up to date".
			Assert.Equal(new[] { "b.txt" }, result.GetProperty("deltaFiles").EnumerateArray().Select(e => e.GetString()).ToArray());
		}
		finally
		{
			Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
		}
	}

	[Theory]
	[InlineData(@"..\outside.txt")]
	[InlineData(@"sub\..\..\outside.txt")]
	[InlineData(@"C:\Windows\win.ini")]
	public async Task Paths_outside_the_site_are_rejected_not_skipped(string path)
	{
		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/compare",
			new { ProjectName = "T", FileHashes = new Dictionary<string, string> { [path] = "00" }, IgnoredFiles = Array.Empty<string>(), MirrorServerToLocal = false });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Unknown_projects_are_rejected()
	{
		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/compare",
			new { ProjectName = "Nope", FileHashes = new Dictionary<string, string>(), IgnoredFiles = Array.Empty<string>(), MirrorServerToLocal = false });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Mirror_with_an_empty_local_list_is_refused()
	{
		using var app = new TestApp();
		Write(app.SitePath("T"), "precious.txt", "do not delete");

		var response = await app.SignedClient().PostAsJsonAsync("api/compare",
			new { ProjectName = "T", FileHashes = new Dictionary<string, string>(), IgnoredFiles = Array.Empty<string>(), MirrorServerToLocal = true });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.True(File.Exists(Path.Combine(app.SitePath("T"), "precious.txt")));
	}

	[Fact]
	public async Task Mirror_counts_extra_files_but_not_ignored_ones_at_any_depth()
	{
		using var app = new TestApp();
		var site = app.SitePath("T");
		Write(site, "a.txt", "same");
		Write(site, "extra.txt", "x");
		Write(site, @"keep\x.txt", "k");
		Write(site, @"logs\old.log", "ignored at root");
		Write(site, @"sub\logs\app.log", "ignored nested");

		var result = await Compare(app.SignedClient(), "T", new() { ["a.txt"] = Hash("same") }, ignored: ["logs/"], mirror: true);

		Assert.Equal(2, result.GetProperty("extraFileCount").GetInt32());
		Assert.NotNull(result.GetProperty("syncManifestId").GetString());
	}

	[Fact]
	public async Task Mirror_deploy_deletes_extras_keeps_ignored_files_and_removes_empty_folders()
	{
		using var app = new TestApp();
		var site = app.SitePath("T");
		Write(site, "a.txt", "same");
		Write(site, "extra.txt", "x");
		Write(site, @"keep\x.txt", "k");
		Write(site, @"logs\old.log", "l1");
		Write(site, @"sub\logs\app.log", "l2");
		var client = app.SignedClient();

		var compare = await Compare(client, "T", new() { ["a.txt"] = Hash("same"), ["new.txt"] = Hash("new") }, ignored: ["logs/"], mirror: true);
		var staged = await UploadHelper.StageAsync(client, "T", new() { ["new.txt"] = "new" }, mirror: true, syncManifestId: compare.GetProperty("syncManifestId").GetString());
		var complete = await UploadHelper.CompleteAsync(client, staged.UploadId);

		Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
		Assert.Equal("new", File.ReadAllText(Path.Combine(site, "new.txt")));
		Assert.Equal("same", File.ReadAllText(Path.Combine(site, "a.txt")));
		Assert.False(File.Exists(Path.Combine(site, "extra.txt")));
		Assert.False(File.Exists(Path.Combine(site, "keep", "x.txt")));
		Assert.False(Directory.Exists(Path.Combine(site, "keep")));
		Assert.True(File.Exists(Path.Combine(site, "logs", "old.log")));
		Assert.True(File.Exists(Path.Combine(site, "sub", "logs", "app.log")));
	}

	[Fact]
	public async Task Mirror_deploy_requires_a_fresh_manifest()
	{
		using var app = new TestApp();
		var client = app.SignedClient();
		var zip = UploadHelper.BuildZip(new() { ["a.txt"] = "a" });

		var response = await client.PostAsJsonAsync("api/upload/sessions", new
		{
			ProjectName = "T", Version = "1", EnableBackup = false, MirrorServerToLocal = true,
			IgnoredFiles = Array.Empty<string>(), SynchronizedFiles = Array.Empty<string>(), SyncManifestId = (string?)null,
			TotalBytes = (long)zip.Length, ChunkSize = 1 << 20, FileHash = ManualRequest.Sha256(zip)
		});

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}
}
