using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastCICD;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FastCICD.Tests;

/// <summary>
/// Hosts the real API in memory with an isolated configuration (temp folders, test key),
/// so tests never touch a developer's local appsettings.json or real sites.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
	public const string Key = "unit-test-key-0123456789-0123456789-abcdef";

	private readonly Dictionary<string, string?> _settings = new();

	public string Root { get; } = Path.Combine(Path.GetTempPath(), "FastCICD-Tests", Guid.NewGuid().ToString("N"));

	/// <param name="projects">Project names to define; each gets an empty site folder.</param>
	/// <param name="extraSettings">Configuration overrides applied last.</param>
	public TestApp(string[]? projects = null, Dictionary<string, string?>? extraSettings = null)
	{
		Directory.CreateDirectory(Root);
		_settings["SecurityKey"] = Key;
		_settings["BackupDirectory"] = Path.Combine(Root, "backups");
		_settings["UploadSessionDirectory"] = Path.Combine(Root, "sessions");
		foreach (var project in projects ?? ["T"])
		{
			var dir = SitePath(project);
			Directory.CreateDirectory(dir);
			_settings[$"AllowedDirectories:{project}"] = dir;
		}
		foreach (var (key, value) in extraSettings ?? new())
			_settings[key] = value;
	}

	public string SitePath(string project) => Path.Combine(Root, "sites", project);
	public string BackupPath(string project) => Path.Combine(Root, "backups", project);

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		// An empty content root keeps the developer's local appsettings.json out of the tests.
		builder.UseContentRoot(Root);
		builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
	}

	/// <summary>A client that signs every request exactly like the real deployment client.</summary>
	public HttpClient SignedClient(string key = Key) => CreateDefaultClient(new HmacDelegatingHandler(key, new HttpClientHandler()));

	/// <summary>A client that signs requests and remembers the headers of the last request it sent.</summary>
	public HttpClient RecordingClient(out RecordingHandler recorder)
	{
		recorder = new RecordingHandler();
		return CreateDefaultClient(new HmacDelegatingHandler(Key, new HttpClientHandler()), recorder);
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (disposing)
		{
			try { Directory.Delete(Root, recursive: true); }
			catch (Exception) { /* Temp folder; a locked file must not fail a test run. */ }
		}
	}
}

public sealed class RecordingHandler : DelegatingHandler
{
	public Dictionary<string, string> LastHeaders { get; private set; } = new();

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		LastHeaders = request.Headers.ToDictionary(h => h.Key, h => h.Value.First());
		return base.SendAsync(request, cancellationToken);
	}
}

/// <summary>
/// Builds signed requests by hand. This intentionally does not reuse HmacDelegatingHandler, so the tests
/// verify the documented protocol rather than just the client agreeing with itself.
/// </summary>
public static class ManualRequest
{
	public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

	public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

	public static string Signature(string key, long timestamp, string nonce, string method, string target, string bodyHash)
	{
		var canonical = string.Join("\n", "FASTCICD-V2", timestamp, nonce, method, target, bodyHash);
		using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
		return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
	}

	/// <param name="signedTarget">Path and query that go into the signature (defaults to <paramref name="target"/>).</param>
	/// <param name="signedBodyHash">Body hash that goes into the signature (defaults to the real body hash).</param>
	public static HttpRequestMessage Create(HttpMethod method, string target, byte[]? body = null, string key = TestApp.Key,
		long? timestamp = null, string? nonce = null, string? signedTarget = null, string? signedBodyHash = null)
	{
		var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		var nonceValue = nonce ?? Guid.NewGuid().ToString("N");
		var bodyHash = signedBodyHash ?? (body is null ? EmptyBodySha256 : Sha256(body));
		var request = new HttpRequestMessage(method, target);
		if (body is not null)
			request.Content = new ByteArrayContent(body);
		request.Headers.Add("X-Timestamp", ts.ToString());
		request.Headers.Add("X-Nonce", nonceValue);
		request.Headers.Add("X-Content-Sha256", bodyHash);
		request.Headers.Add("X-Signature", Signature(key, ts, nonceValue, method.Method, signedTarget ?? target, bodyHash));
		return request;
	}
}

public static class UploadHelper
{
	public sealed record Staged(string UploadId, byte[] Zip, string ZipHash);

	/// <summary>Creates an upload session and sends every chunk (unless told not to).</summary>
	public static async Task<Staged> StageAsync(HttpClient client, string project, Dictionary<string, string> files, int chunkSize = 1 << 20,
		bool mirror = false, string? syncManifestId = null, string? declaredHash = null, bool sendChunks = true)
	{
		var zip = BuildZip(files);
		var zipHash = ManualRequest.Sha256(zip);
		using var created = await client.PostAsJsonAsync("api/upload/sessions", new
		{
			ProjectName = project,
			Version = "1",
			EnableBackup = false,
			MirrorServerToLocal = mirror,
			IgnoredFiles = Array.Empty<string>(),
			SynchronizedFiles = Array.Empty<string>(),
			SyncManifestId = syncManifestId,
			TotalBytes = (long)zip.Length,
			ChunkSize = chunkSize,
			FileHash = declaredHash ?? zipHash
		});
		created.EnsureSuccessStatusCode();
		var uploadId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;

		if (sendChunks)
		{
			for (var offset = 0; offset < zip.Length; offset += chunkSize)
			{
				var part = zip.Skip(offset).Take(chunkSize).ToArray();
				var response = await SendChunkAsync(client, uploadId, offset / chunkSize, part, ManualRequest.Sha256(part));
				response.EnsureSuccessStatusCode();
			}
		}

		return new Staged(uploadId, zip, zipHash);
	}

	public static async Task<HttpResponseMessage> SendChunkAsync(HttpClient client, string uploadId, int index, byte[] body, string declaredHash)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"api/upload/sessions/{uploadId}/chunks/{index}") { Content = new ByteArrayContent(body) };
		request.Headers.Add(HmacDelegatingHandler.BodyHashHeader, declaredHash);
		return await client.SendAsync(request);
	}

	public static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string uploadId)
		=> client.PostAsync($"api/upload/sessions/{uploadId}/complete", content: null);

	public static byte[] BuildZip(Dictionary<string, string> files)
	{
		using var stream = new MemoryStream();
		using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (name, content) in files)
			{
				var entry = archive.CreateEntry(name);
				using var writer = new StreamWriter(entry.Open());
				writer.Write(content);
			}
		}
		return stream.ToArray();
	}
}
