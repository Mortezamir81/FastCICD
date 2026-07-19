using CICD_API.Endpoints;
using CICD_API.Middlewares;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
	options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
	// Clear known networks/proxies to trust Nginx running on the same local machine
	options.KnownNetworks.Clear();
	options.KnownProxies.Clear();
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

var app = builder.Build();

// Security Middleware: Dual Authentication (Static IP vs Dynamic HMAC)
app.UseMiddleware<SecurityAuthMiddleware>();

app.MapDeployEndpoints();

app.Run();