using CICD_API.Endpoints;
using CICD_API.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Increase maximum request body size for Kestrel (Set to 100 MB)
builder.WebHost.ConfigureKestrel(serverOptions =>
{
	serverOptions.Limits.MaxRequestBodySize = 104857600;
});

// Increase multipart form body length limit for file uploads (Set to 100 MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
	options.MultipartBodyLengthLimit = 104857600;
});

var app = builder.Build();

// Security Middleware: Dual Authentication (Static IP vs Dynamic HMAC)
app.UseMiddleware<SecurityAuthMiddleware>();

app.MapDeployEndpoints();

app.Run();