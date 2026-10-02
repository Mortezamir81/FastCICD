using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace FastCICD.Tests;

public class AuthenticationTests
{
	private const string Target = "/api/version?projectName=T";

	[Fact]
	public async Task Signed_request_is_accepted()
	{
		using var app = new TestApp();
		var response = await app.SignedClient().GetAsync(Target);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Unsigned_request_is_rejected()
	{
		using var app = new TestApp();
		var response = await app.CreateClient().GetAsync(Target);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Wrong_key_is_rejected()
	{
		using var app = new TestApp();
		var response = await app.SignedClient("another-key-another-key-another-key-0000").GetAsync(Target);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task The_key_itself_is_never_sent()
	{
		using var app = new TestApp();
		var client = app.RecordingClient(out var recorder);
		await client.GetAsync(Target);

		Assert.DoesNotContain(recorder.LastHeaders, header => header.Value.Contains(TestApp.Key));
		Assert.False(recorder.LastHeaders.ContainsKey("X-Api-Key"));
	}

	[Fact]
	public async Task A_captured_request_cannot_be_replayed()
	{
		using var app = new TestApp();
		var client = app.RecordingClient(out var recorder);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Target)).StatusCode);

		var replay = new HttpRequestMessage(HttpMethod.Get, Target);
		foreach (var name in new[] { "X-Timestamp", "X-Nonce", "X-Content-Sha256", "X-Signature" })
			replay.Headers.Add(name, recorder.LastHeaders[name]);
		var response = await app.CreateClient().SendAsync(replay);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task A_signature_cannot_be_reused_for_another_path()
	{
		using var app = new TestApp();
		var request = ManualRequest.Create(HttpMethod.Get, "/api/backups?projectName=T", signedTarget: Target);
		var response = await app.CreateClient().SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task A_signature_cannot_be_reused_for_another_query()
	{
		using var app = new TestApp(["T", "Other"]);
		var request = ManualRequest.Create(HttpMethod.Get, "/api/version?projectName=Other", signedTarget: Target);
		var response = await app.CreateClient().SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task A_signature_cannot_be_reused_for_another_method()
	{
		using var app = new TestApp();
		var request = ManualRequest.Create(HttpMethod.Post, "/api/services", Encoding.UTF8.GetBytes("{}"));
		var forged = new HttpRequestMessage(HttpMethod.Put, "/api/services") { Content = request.Content };
		foreach (var header in request.Headers)
			forged.Headers.Add(header.Key, header.Value);
		var response = await app.CreateClient().SendAsync(forged);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task A_body_that_differs_from_the_signed_hash_is_rejected()
	{
		using var app = new TestApp();
		var signedFor = ManualRequest.Sha256(Encoding.UTF8.GetBytes("""{"Services":[],"Action":"status"}"""));
		var request = ManualRequest.Create(HttpMethod.Post, "/api/services", Encoding.UTF8.GetBytes("""{"Services":[],"Action":"stop"}"""), signedBodyHash: signedFor);
		request.Content!.Headers.ContentType = new("application/json");
		var response = await app.CreateClient().SendAsync(request);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData(-3600)]
	[InlineData(3600)]
	public async Task Requests_outside_the_time_window_are_rejected(int offsetSeconds)
	{
		using var app = new TestApp();
		var request = ManualRequest.Create(HttpMethod.Get, Target, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds);
		var response = await app.CreateClient().SendAsync(request);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task A_manually_signed_request_follows_the_documented_protocol()
	{
		using var app = new TestApp();
		var response = await app.CreateClient().SendAsync(ManualRequest.Create(HttpMethod.Get, Target));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("short")]
	[InlineData("has spaces in it which is not allowed")]
	public async Task Malformed_nonce_is_rejected(string nonce)
	{
		using var app = new TestApp();
		var response = await app.CreateClient().SendAsync(ManualRequest.Create(HttpMethod.Get, Target, nonce: nonce));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Only_listed_client_addresses_are_accepted_when_a_list_is_configured()
	{
		using var app = new TestApp(extraSettings: new() { ["AllowedClientIps:0"] = "10.9.9.9" });
		var response = await app.SignedClient().GetAsync(Target);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task A_missing_server_key_fails_closed()
	{
		using var app = new TestApp(extraSettings: new() { ["SecurityKey"] = "" });
		var response = await app.SignedClient().GetAsync(Target);
		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
	}

	[Fact]
	public async Task Every_endpoint_requires_authentication()
	{
		using var app = new TestApp();
		var anonymous = app.CreateClient();
		var probes = new (HttpMethod Method, string Path)[]
		{
			(HttpMethod.Post, "/api/compare"), (HttpMethod.Post, "/api/services"), (HttpMethod.Get, "/api/backups?projectName=T"),
			(HttpMethod.Post, "/api/rollback"), (HttpMethod.Get, "/api/version?projectName=T"), (HttpMethod.Post, "/api/execute"),
			(HttpMethod.Post, "/api/upload/sessions"), (HttpMethod.Get, "/api/upload/sessions/00000000000000000000000000000000"),
			(HttpMethod.Post, "/api/upload/sessions/00000000000000000000000000000000/chunks/0"),
			(HttpMethod.Post, "/api/upload/sessions/00000000000000000000000000000000/complete"),
			(HttpMethod.Get, "/api/migrations/x"), (HttpMethod.Post, "/api/migrations/x/apply"),
		};

		foreach (var (method, path) in probes)
		{
			var response = await anonymous.SendAsync(new HttpRequestMessage(method, path));
			Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode}");
		}
	}
}
