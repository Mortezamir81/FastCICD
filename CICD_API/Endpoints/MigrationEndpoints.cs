using CICD_API.Migrations;

namespace CICD_API.Endpoints;

public static class MigrationEndpoints
{
	private const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

	public static void MapMigrationEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPut("/api/migrations/{profileName}/script", async (string profileName, HttpRequest request, MigrationRequestAuthenticator authenticator, IMigrationManager manager, ILogger<MigrationManager> logger, CancellationToken cancellationToken) =>
		{
			if (!TryAuthorize(request, authenticator, out var contentSha256, out var denied)) return denied;
			if (!IsSqlContent(request.ContentType) || request.ContentLength is <= 0) return Results.BadRequest("A non-empty SQL script is required.");
			try { return Results.Ok(await manager.UploadScriptAsync(profileName, request.Body, contentSha256, cancellationToken)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
			catch (InvalidOperationException exception) { return Results.BadRequest(exception.Message); }
			catch (Exception exception) { logger.LogError(exception, "Unable to store migration script for profile '{ProfileName}'.", profileName); return Results.Problem("Unable to store the migration script. Check the server logs."); }
		})
		.WithName("UploadMigrationScript")
		.WithSummary("Securely upload an EF Core SQL migration script to the server-managed migration store.");

		app.MapGet("/api/migrations/{profileName}", async (string profileName, HttpRequest request, MigrationRequestAuthenticator authenticator, IMigrationManager manager, ILogger<MigrationManager> logger, CancellationToken cancellationToken) =>
		{
			if (!TryAuthorize(request, authenticator, out _, out var denied)) return denied;
			try { return Results.Ok(await manager.GetStatusAsync(profileName, cancellationToken)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (FileNotFoundException) { return Results.NotFound(); }
			catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
			catch (Exception exception) { logger.LogError(exception, "Unable to read migration status for profile '{ProfileName}'.", profileName); return Results.Problem("Unable to read migration status. Check the server logs."); }
		})
		.WithName("GetMigrationStatus")
		.WithSummary("List migration history for the server-stored script.");

		app.MapPost("/api/migrations/{profileName}/apply", async (string profileName, HttpRequest request, MigrationRequestAuthenticator authenticator, IMigrationManager manager, ILogger<MigrationManager> logger, CancellationToken cancellationToken) =>
		{
			if (!TryAuthorize(request, authenticator, out _, out var denied)) return denied;
			try { return Results.Ok(await manager.ApplyPendingAsync(profileName, cancellationToken)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (FileNotFoundException) { return Results.NotFound(); }
			catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
			catch (Exception exception) { logger.LogError(exception, "Migration execution failed for profile '{ProfileName}'.", profileName); return Results.Problem("Migration execution failed. No later migrations were attempted; check the server logs."); }
		})
		.WithName("ApplyPendingMigrations")
		.WithSummary("Apply pending migrations from the server-stored script.");
	}

	private static bool TryAuthorize(HttpRequest request, MigrationRequestAuthenticator authenticator, out string bodySha256, out IResult denied)
	{
		bodySha256 = request.Method == HttpMethods.Put ? request.Headers["X-Migration-Content-Sha256"].ToString() : EmptyBodySha256;
		if (authenticator.IsHttpsRequired && !request.IsHttps)
		{
			denied = Results.NotFound();
			return false;
		}
		if (!authenticator.TryAuthenticate(request, bodySha256, out _))
		{
			denied = Results.NotFound();
			return false;
		}
		denied = Results.Empty;
		return true;
	}

	private static bool IsSqlContent(string? contentType) => contentType is not null &&
		(contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("application/sql", StringComparison.OrdinalIgnoreCase));
}
