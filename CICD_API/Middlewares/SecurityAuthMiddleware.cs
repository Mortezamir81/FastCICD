using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CICD_API.Middlewares;

/// <summary>
/// Authenticates every request with an HMAC-SHA256 signature over timestamp, nonce, method, path, query and body hash.
/// The shared SecurityKey itself is never sent over the wire.
/// </summary>
public class SecurityAuthMiddleware
{
	public const string SignaturePrefix = "FASTCICD-V2";
	public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
	private const long MaxBufferedBodyBytes = 64L * 1024 * 1024;

	private readonly RequestDelegate _next;
	private readonly IConfiguration _config;
	private readonly ILogger<SecurityAuthMiddleware> _logger;
	private readonly ConcurrentDictionary<string, DateTimeOffset> _usedNonces = new(StringComparer.Ordinal);
	private long _requestCounter;

	public SecurityAuthMiddleware(RequestDelegate next, IConfiguration config, ILogger<SecurityAuthMiddleware> logger)
	{
		_next = next;
		_config = config;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		var request = context.Request;
		var remoteIp = context.Connection.RemoteIpAddress;
		var requestId = request.Headers["X-Request-Id"].FirstOrDefault() ?? context.TraceIdentifier;

		_logger.LogInformation(
			"Request received. RequestId: {RequestId}; Method: {Method}; Path: {Path}; RemoteIp: {RemoteIp}; ContentLength: {ContentLength}",
			requestId, request.Method, request.Path, remoteIp, request.ContentLength);

		// 1. Optional network restriction: when AllowedClientIps is set, only those addresses may connect.
		var allowedIps = _config.GetSection("AllowedClientIps").Get<List<string>>() ?? [];
		if (allowedIps.Count > 0 && !IsAllowedIp(remoteIp, allowedIps))
		{
			await RejectAsync(context, StatusCodes.Status403Forbidden, "Client address is not allowed.", requestId, "IP not allowed");
			return;
		}

		var key = _config["SecurityKey"];
		if (string.IsNullOrWhiteSpace(key))
		{
			await RejectAsync(context, StatusCodes.Status503ServiceUnavailable, "Deployment authentication is not configured.", requestId, "Security key is not configured");
			return;
		}

		// 2. Required signature headers.
		if (!request.Headers.TryGetValue("X-Timestamp", out var timestampHeader) ||
			!request.Headers.TryGetValue("X-Nonce", out var nonceHeader) ||
			!request.Headers.TryGetValue("X-Content-Sha256", out var bodyHashHeader) ||
			!request.Headers.TryGetValue("X-Signature", out var signatureHeader))
		{
			await RejectAsync(context, StatusCodes.Status401Unauthorized, "Missing authentication headers.", requestId, "Missing signature headers");
			return;
		}

		var nonce = nonceHeader.ToString();
		var declaredBodyHash = bodyHashHeader.ToString().ToLowerInvariant();
		if (!long.TryParse(timestampHeader, out var timestamp) || !IsValidNonce(nonce) || !IsSha256Hex(declaredBodyHash))
		{
			await RejectAsync(context, StatusCodes.Status401Unauthorized, "Malformed authentication headers.", requestId, "Malformed signature headers");
			return;
		}

		// 3. Timestamp window.
		var validityMinutes = GetRequestValidityMinutes(request.Path);
		var ageMinutes = (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp)).TotalMinutes;
		if (Math.Abs(ageMinutes) > validityMinutes)
		{
			await RejectAsync(context, StatusCodes.Status401Unauthorized,
				$"Request has expired. Allowed age is {validityMinutes} minutes. Please check system clocks.", requestId, $"Expired request (age {ageMinutes:F2} min)");
			return;
		}

		// 4. Signature over everything that defines the request.
		var canonical = string.Join("\n", SignaturePrefix, timestampHeader.ToString(), nonce, request.Method.ToUpperInvariant(),
			request.Path.Value + request.QueryString.Value, declaredBodyHash);
		using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
		var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
		if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signatureHeader.ToString()), Encoding.UTF8.GetBytes(expected)))
		{
			await RejectAsync(context, StatusCodes.Status403Forbidden, "Invalid signature.", requestId, "Invalid signature");
			return;
		}

		// 5. Single-use nonce: a captured request cannot be replayed.
		if (!TryRememberNonce(nonce, TimeSpan.FromMinutes(validityMinutes * 2 + 1)))
		{
			await RejectAsync(context, StatusCodes.Status401Unauthorized, "Request was already used.", requestId, "Replayed nonce");
			return;
		}

		// 6. Make sure the body really is the one that was signed.
		// Chunk and migration bodies are verified by their own endpoints while streaming.
		if (!IsStreamedBodyPath(request.Path))
		{
			var failure = await VerifyBodyAsync(context, declaredBodyHash);
			if (failure is not null)
			{
				await RejectAsync(context, failure.Value, "Request body does not match its signed hash.", requestId, "Body hash mismatch");
				return;
			}
		}

		await _next(context);
	}

	private static bool IsStreamedBodyPath(PathString path) =>
		path.StartsWithSegments("/api/migrations") ||
		(path.StartsWithSegments("/api/upload/sessions") && path.Value!.Contains("/chunks/", StringComparison.Ordinal));

	private static async Task<int?> VerifyBodyAsync(HttpContext context, string declaredHash)
	{
		var request = context.Request;
		string actual;
		if (request.ContentLength == 0)
		{
			actual = EmptyBodySha256;
		}
		else
		{
			if (request.ContentLength > MaxBufferedBodyBytes)
				return StatusCodes.Status413PayloadTooLarge;
			try
			{
				request.EnableBuffering(bufferThreshold: 1024 * 1024, bufferLimit: MaxBufferedBodyBytes);
				actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(request.Body, context.RequestAborted));
				request.Body.Position = 0;
			}
			catch (InvalidDataException)
			{
				return StatusCodes.Status413PayloadTooLarge;
			}
		}

		return string.Equals(actual, declaredHash, StringComparison.Ordinal) ? null : StatusCodes.Status401Unauthorized;
	}

	private bool TryRememberNonce(string nonce, TimeSpan lifetime)
	{
		var now = DateTimeOffset.UtcNow;
		if (Interlocked.Increment(ref _requestCounter) % 200 == 0)
		{
			foreach (var entry in _usedNonces.Where(e => e.Value <= now))
				_usedNonces.TryRemove(entry.Key, out _);
		}
		return _usedNonces.TryAdd(nonce, now.Add(lifetime));
	}

	private static bool IsAllowedIp(IPAddress? remoteIp, List<string> allowed)
	{
		if (remoteIp is null)
			return false;
		if (remoteIp.IsIPv4MappedToIPv6)
			remoteIp = remoteIp.MapToIPv4();

		foreach (var candidate in allowed)
		{
			if (!IPAddress.TryParse(candidate, out var ip))
				continue;
			if (ip.IsIPv4MappedToIPv6)
				ip = ip.MapToIPv4();
			if (ip.Equals(remoteIp))
				return true;
		}
		return false;
	}

	private static bool IsValidNonce(string nonce) =>
		nonce.Length is >= 16 and <= 64 && nonce.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

	private static bool IsSha256Hex(string value) =>
		value.Length == 64 && value.All(char.IsAsciiHexDigit);

	private async Task RejectAsync(HttpContext context, int statusCode, string message, string requestId, string reason)
	{
		_logger.LogWarning(
			"Request rejected. RequestId: {RequestId}; Status: {Status}; Reason: {Reason}; Method: {Method}; Path: {Path}; RemoteIp: {RemoteIp}",
			requestId, statusCode, reason, context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
		context.Response.StatusCode = statusCode;
		await context.Response.WriteAsync(message);
	}

	private int GetRequestValidityMinutes(PathString path)
	{
		var settingKey = path.StartsWithSegments("/api/upload")
			? "UploadHmacValidityMinutes"
			: "HmacValidityMinutes";

		var defaultValue = settingKey == "UploadHmacValidityMinutes" ? 15 : 5;
		var configuredValue = _config.GetValue<int?>(settingKey);
		return configuredValue is > 0 and <= 1440 ? configuredValue.Value : defaultValue;
	}
}
