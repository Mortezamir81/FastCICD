using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FastCICD;

// HTTP Handler to automatically sign all outgoing requests with HMAC
public class HmacDelegatingHandler : DelegatingHandler
{
	private readonly string _secretKey;

	public HmacDelegatingHandler(string secretKey, HttpMessageHandler innerHandler) : base(innerHandler)
	{
		_secretKey = secretKey;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// Generate current Unix timestamp
		var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

		// Generate HMAC SHA256 Signature
		var keyBytes = System.Text.Encoding.UTF8.GetBytes(_secretKey);
		using var hmac = new HMACSHA256(keyBytes);
		var signature = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(timestamp)));

		// Inject signature headers into the request
		request.Headers.Add("X-Timestamp", timestamp);
		request.Headers.Add("X-Signature", signature);

		return await base.SendAsync(request, cancellationToken);
	}
}
