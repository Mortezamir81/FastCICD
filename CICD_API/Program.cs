using CICD_API.Endpoints;
using CICD_API.Middlewares;
using CICD_API.Migrations;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

	builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
	options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
	// Keep ASP.NET Core's loopback proxy defaults. Add explicitly trusted reverse
	// proxy addresses if the proxy is not on the same host.
});

// 1. Remove request size limit for Kestrel server
builder.WebHost.ConfigureKestrel(options =>
{
	options.Limits.MaxRequestBodySize = null; // Unlimited size
});

// 2. Remove multipart form size limit for file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
	options.MultipartBodyLengthLimit = long.MaxValue;
});

var configuredUploadDiagnosticsLogPath = builder.Configuration["UploadDiagnosticsLogPath"];
string uploadDiagnosticsLogPath = string.IsNullOrWhiteSpace(configuredUploadDiagnosticsLogPath)
	? Path.Combine(builder.Configuration["BackupDirectory"] ?? Path.GetTempPath(), "fastcicd-upload-diagnostics.log")
	: configuredUploadDiagnosticsLogPath!;
var uploadDiagnosticsLogLock = new object();

builder.Services.AddSingleton<IMigrationManager, MigrationManager>();
builder.Services.AddSingleton<MigrationRequestAuthenticator>();

var app = builder.Build();

app.UseForwardedHeaders();
app.Logger.LogInformation("Upload diagnostics file configured at {UploadDiagnosticsLogPath}", uploadDiagnosticsLogPath);

app.Use(async (context, next) =>
{
	try
	{
		await next(context);
	}
	catch (Exception exception)
	{
		var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault() ?? context.TraceIdentifier;
		app.Logger.LogError(exception, "Unhandled API exception. RequestId: {RequestId}; Method: {Method}; Path: {Path}; ContentLength: {ContentLength}", requestId, context.Request.Method, context.Request.Path, context.Request.ContentLength);
		throw;
	}
});

app.Use(async (context, next) =>
{
	if (!context.Request.Path.StartsWithSegments("/api/upload"))
	{
		await next(context);
		return;
	}

	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault() ?? context.TraceIdentifier;
	try
	{
		await next(context);
	}
	finally
	{
		stopwatch.Stop();
		var diagnosticLine = $"{DateTimeOffset.UtcNow:O} RequestId={requestId}; Method={context.Request.Method}; Path={context.Request.Path}; Status={context.Response.StatusCode}; ContentLength={context.Request.ContentLength}; ElapsedMs={stopwatch.ElapsedMilliseconds}; RemoteIp={context.Connection.RemoteIpAddress}";
		app.Logger.LogInformation(
			"Upload HTTP completed. RequestId: {RequestId}; Method: {Method}; Path: {Path}; Status: {StatusCode}; ContentLength: {ContentLength}; ElapsedMs: {ElapsedMs}; RemoteIp: {RemoteIp}",
			requestId,
			context.Request.Method,
			context.Request.Path,
			context.Response.StatusCode,
			context.Request.ContentLength,
			stopwatch.ElapsedMilliseconds,
			context.Connection.RemoteIpAddress);
		try
		{
			var logDirectory = Path.GetDirectoryName(uploadDiagnosticsLogPath);
			if (!string.IsNullOrWhiteSpace(logDirectory))
				Directory.CreateDirectory(logDirectory);
			lock (uploadDiagnosticsLogLock)
				File.AppendAllText(uploadDiagnosticsLogPath, diagnosticLine + Environment.NewLine);
		}
		catch (Exception exception)
		{
			app.Logger.LogWarning(exception, "Could not write upload diagnostics file {UploadDiagnosticsLogPath}", uploadDiagnosticsLogPath);
		}
	}
});

// Security Middleware: Dual Authentication (Static IP vs Dynamic HMAC)
app.UseMiddleware<SecurityAuthMiddleware>();

app.MapDeployEndpoints();
app.MapMigrationEndpoints();

app.Run();
