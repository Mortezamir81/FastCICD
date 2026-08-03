using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace CICD_API.Middlewares;

public class SecurityAuthMiddleware
{
	private readonly RequestDelegate _next;
	private readonly IConfiguration _config;
	private readonly ILogger<SecurityAuthMiddleware> _logger;

	public SecurityAuthMiddleware(RequestDelegate next, IConfiguration config, ILogger<SecurityAuthMiddleware> logger)
	{
		_next = next;
		_config = config;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		var allowedIpStr = _config["AllowedClientIp"];
		var expectedKey = _config["SecurityKey"];
		var remoteIp = context.Connection.RemoteIpAddress;
		var requestValidityMinutes = GetRequestValidityMinutes(context.Request.Path);

		_logger.LogInformation("Remote IP Requested: {RemoteIp}", remoteIp);

		bool isIpValid = false;

		// 1. Check if the IP matches the allowed static IP
		if (!string.IsNullOrEmpty(allowedIpStr) && remoteIp != null)
		{
			var allowedIp = IPAddress.Parse(allowedIpStr);

			if (remoteIp.IsIPv4MappedToIPv6)
				remoteIp = remoteIp.MapToIPv4();
			if (allowedIp.IsIPv4MappedToIPv6)
				allowedIp = allowedIp.MapToIPv4();

			if (remoteIp.Equals(allowedIp))
			{
				isIpValid = true;
			}
		}

		// 2. Fast Path: If IP is valid, just check the simple API Key
		if (isIpValid)
		{
			if (context.Request.Headers.TryGetValue("X-Api-Key", out var extractedKey) && extractedKey == expectedKey)
			{
				await _next(context);
				return;
			}

			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			await context.Response.WriteAsync("Unauthorized: Invalid API Key for the allowed IP.");
			return;
		}

		// 3. Fallback Path: For dynamic/unknown IPs, require HMAC Signature
		if (context.Request.Headers.TryGetValue("X-Timestamp", out var timestampStr) &&
			context.Request.Headers.TryGetValue("X-Signature", out var clientSignature))
		{
			if (!long.TryParse(timestampStr, out var timestamp))
			{
				context.Response.StatusCode = StatusCodes.Status401Unauthorized;
				await context.Response.WriteAsync("Unauthorized: Invalid timestamp format.");
				return;
			}

			var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
			var requestAge = DateTimeOffset.UtcNow - requestTime;
			if (Math.Abs(requestAge.TotalMinutes) > requestValidityMinutes)
			{
				_logger.LogWarning(
					"Rejected expired HMAC request for {Path}. Age: {RequestAgeMinutes:F2} minutes; allowed: {AllowedMinutes} minutes.",
					context.Request.Path,
					requestAge.TotalMinutes,
					requestValidityMinutes);
				context.Response.StatusCode = StatusCodes.Status401Unauthorized;
				await context.Response.WriteAsync(
					$"Unauthorized: Request has expired. Allowed age is {requestValidityMinutes} minutes. Please check system clocks.");
				return;
			}

			var payload = timestampStr.ToString();
			var keyBytes = Encoding.UTF8.GetBytes(expectedKey!);
			using var hmac = new HMACSHA256(keyBytes);
			var serverSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

			var providedSignatureBytes = Encoding.UTF8.GetBytes(clientSignature.ToString());
			var expectedSignatureBytes = Encoding.UTF8.GetBytes(serverSignature);
			if (CryptographicOperations.FixedTimeEquals(providedSignatureBytes, expectedSignatureBytes))
			{
				await _next(context);
				return;
			}
		}

		// 4. If neither IP matched nor valid HMAC was provided
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
		await context.Response.WriteAsync("Access Denied: Invalid IP and no valid security signature provided.");
	}

	private int GetRequestValidityMinutes(PathString path)
	{
		var settingKey = path.StartsWithSegments("/api/upload")
			? "UploadHmacValidityMinutes"
			: "HmacValidityMinutes";

		var defaultValue = settingKey == "UploadHmacValidityMinutes" ? 120 : 5;
		var configuredValue = _config.GetValue<int?>(settingKey);
		return configuredValue is > 0 and <= 1440 ? configuredValue.Value : defaultValue;
	}
}
