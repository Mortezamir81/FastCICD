using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FastCICD.Tests;

public class EndpointTests
{
	[Theory]
	[InlineData("api/backups?projectName=Nope")]
	[InlineData(@"api/backups?projectName=..%5Capi")]
	[InlineData("api/version?projectName=Nope")]
	[InlineData("api/version?projectName=..%5C..")]
	public async Task Project_names_must_be_defined_on_the_server(string url)
	{
		using var app = new TestApp();
		var response = await app.SignedClient().GetAsync(url);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Version_is_reported_even_before_the_first_deployment()
	{
		using var app = new TestApp();
		var version = await app.SignedClient().GetFromJsonAsync<JsonElement>("api/version?projectName=T");
		Assert.Equal("No version info", version.GetProperty("version").GetString());
	}

	[Fact]
	public async Task Unknown_service_actions_are_rejected()
	{
		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/services", new { Services = Array.Empty<string>(), Action = "restart" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Services_that_are_not_allow_listed_cannot_be_managed()
	{
		using var app = new TestApp(extraSettings: new() { ["AllowedServices:0"] = "AllowedOne" });
		var response = await app.SignedClient().PostAsJsonAsync("api/services", new { Services = new[] { "SomeOtherService" }, Action = "stop" });
		Assert.False(response.IsSuccessStatusCode);
	}

	[Fact]
	public async Task Status_of_an_allow_listed_service_that_does_not_exist_is_reported()
	{
		using var app = new TestApp(extraSettings: new() { ["AllowedServices:0"] = "FastCicdTestServiceThatDoesNotExist" });
		var statuses = await app.SignedClient().PostAsJsonAsync("api/services", new { Services = new[] { "FastCicdTestServiceThatDoesNotExist" }, Action = "status" });
		var body = await statuses.Content.ReadFromJsonAsync<Dictionary<string, string>>();
		Assert.Equal("Not Found", body!["FastCicdTestServiceThatDoesNotExist"]);
	}

	[Fact]
	public async Task Remote_commands_stream_camel_case_events_the_client_can_read()
	{
		if (!OperatingSystem.IsWindows())
			return; // The server runs commands through cmd.exe.

		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/execute", new { ProjectName = "T", Commands = new[] { "echo hello" } });
		var events = ParseEvents(await response.Content.ReadAsStringAsync());

		// Regression: keys used to be PascalCase, so the client never saw outputs or errors.
		Assert.Contains(events, e => e.GetProperty("type").GetString() == "started");
		Assert.Contains(events, e => e.GetProperty("type").GetString() == "output" && e.GetProperty("message").GetString() == "hello");
		Assert.Contains(events, e => e.GetProperty("type").GetString() == "completed");
		Assert.Equal("finished", events[^1].GetProperty("type").GetString());
	}

	[Fact]
	public async Task A_failing_remote_command_is_reported_as_an_error_event_and_stops_the_run()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/execute", new { ProjectName = "T", Commands = new[] { "exit 3", "echo should-not-run" } });
		var events = ParseEvents(await response.Content.ReadAsStringAsync());

		var error = Assert.Single(events, e => e.GetProperty("type").GetString() == "error");
		Assert.Contains("exit code 3", error.GetProperty("message").GetString());
		Assert.DoesNotContain(events, e => e.TryGetProperty("message", out var m) && m.GetString() == "should-not-run");
	}

	[Fact]
	public async Task Remote_commands_run_inside_the_project_folder()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/execute", new { ProjectName = "T", Commands = new[] { "cd" } });
		var events = ParseEvents(await response.Content.ReadAsStringAsync());

		var output = events.First(e => e.GetProperty("type").GetString() == "output").GetProperty("message").GetString();
		Assert.Equal(app.SitePath("T").TrimEnd('\\'), output!.TrimEnd('\\'), ignoreCase: true);
	}

	[Fact]
	public async Task Remote_commands_for_an_unknown_project_are_refused()
	{
		using var app = new TestApp();
		var response = await app.SignedClient().PostAsJsonAsync("api/execute", new { ProjectName = "Nope", Commands = new[] { "echo hi" } });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	private static List<JsonElement> ParseEvents(string ndjson)
		=> ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(line => JsonDocument.Parse(line).RootElement.Clone())
			.ToList();
}
