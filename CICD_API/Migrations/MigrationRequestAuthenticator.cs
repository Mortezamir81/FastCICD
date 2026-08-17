using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CICD_API.Migrations;

public sealed class MigrationRequestAuthenticator(IConfiguration configuration)
{
	private readonly ConcurrentDictionary<string, DateTimeOffset> usedNonces = new(StringComparer.Ordinal);

	public bool IsHttpsRequired => configuration.GetValue("RequireHttpsForMigrations", true);

	public bool TryAuthenticate(HttpRequest request, string bodySha256, out string error)
	{
		error = "";
		var key = configuration["MigrationExecutionKey"];
		if (string.IsNullOrWhiteSpace(key) || !request.Headers.TryGetValue("X-Migration-Key", out var suppliedKey) ||
			!FixedTimeEquals(key, suppliedKey.ToString()))
		{
			error = "Migration access is not configured.";
			return false;
		}
		if (!request.Headers.TryGetValue("X-Migration-Timestamp", out var timestampHeader) ||
			!long.TryParse(timestampHeader, out var unixTimestamp) ||
			Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTimestamp)).TotalMinutes) > 2)
		{
			error = "Migration request expired.";
			return false;
		}
		if (!request.Headers.TryGetValue("X-Migration-Nonce", out var nonceHeader) || nonceHeader.Count != 1 || !Guid.TryParse(nonceHeader, out _) ||
			!request.Headers.TryGetValue("X-Migration-Signature", out var signatureHeader))
		{
			error = "Migration request signature is invalid.";
			return false;
		}

		var canonical = string.Join("\n", timestampHeader.ToString(), nonceHeader.ToString(), request.Method, request.Path.Value ?? "", bodySha256);
		using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
		var expectedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
		if (!FixedTimeEquals(expectedSignature, signatureHeader.ToString()))
		{
			error = "Migration request signature is invalid.";
			return false;
		}

		var now = DateTimeOffset.UtcNow;
		foreach (var entry in usedNonces.Where(entry => entry.Value <= now))
			usedNonces.TryRemove(entry.Key, out _);
		if (!usedNonces.TryAdd(nonceHeader.ToString(), now.AddMinutes(3)))
		{
			error = "Migration request was already used.";
			return false;
		}
		return true;
	}

	private static bool FixedTimeEquals(string expected, string supplied) =>
		CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
}
