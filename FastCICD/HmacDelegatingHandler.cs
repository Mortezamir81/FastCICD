using System.Security.Cryptography;
using System.Text;

namespace FastCICD;

// HTTP handler that signs every outgoing request. The secret key is never sent; only an HMAC over
// timestamp, nonce, method, path, query and body hash travels with the request.
public class HmacDelegatingHandler : DelegatingHandler
{
	public const string BodyHashHeader = "X-Content-Sha256";
	private const string SignaturePrefix = "FASTCICD-V2";
	private const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

	private readonly Func<string> _secretKeyProvider;

	public HmacDelegatingHandler(string secretKey, HttpMessageHandler innerHandler) : base(innerHandler)
	{
		_secretKeyProvider = () => secretKey;
	}

	public HmacDelegatingHandler(Func<string> secretKeyProvider, HttpMessageHandler innerHandler) : base(innerHandler)
	{
		_secretKeyProvider = secretKeyProvider;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
		var nonce = Guid.NewGuid().ToString("N");
		var bodyHash = await GetBodyHashAsync(request, cancellationToken);

		// Sign the route (from "/api/" onward) so a path prefix added by a reverse proxy does not break verification.
		var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no URI.");
		var path = Uri.UnescapeDataString(uri.AbsolutePath);
		var apiIndex = path.IndexOf("/api/", StringComparison.Ordinal);
		if (apiIndex > 0)
			path = path[apiIndex..];

		var canonical = string.Join("\n", SignaturePrefix, timestamp, nonce, request.Method.Method.ToUpperInvariant(), path + uri.Query, bodyHash);
		using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKeyProvider()));
		var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

		request.Headers.Remove(BodyHashHeader);
		request.Headers.Add(BodyHashHeader, bodyHash);
		request.Headers.Add("X-Timestamp", timestamp);
		request.Headers.Add("X-Nonce", nonce);
		request.Headers.Add("X-Signature", signature);
		request.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString("N"));

		return await base.SendAsync(request, cancellationToken);
	}

	// Callers that stream large bodies (upload chunks, migration scripts) supply the hash themselves;
	// everything else is small enough to buffer and hash here.
	private static async Task<string> GetBodyHashAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.Headers.TryGetValues(BodyHashHeader, out var provided))
			return provided.First().ToLowerInvariant();
		if (request.Content is null)
			return EmptyBodySha256;
		if (request.Headers.TryGetValues("X-Migration-Content-Sha256", out var migrationHash))
			return migrationHash.First().ToLowerInvariant();

		await request.Content.LoadIntoBufferAsync(cancellationToken);
		var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
		return Convert.ToHexStringLower(SHA256.HashData(bytes));
	}
}
