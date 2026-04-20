namespace FastCICD;

public static class HttpResponseExtensions
{
	public static async Task EnsureSuccessWithDetailsAsync(this HttpResponseMessage response)
	{
		if (!response.IsSuccessStatusCode)
		{
			var errorContent = await response.Content.ReadAsStringAsync();
			throw new Exception($"HTTP {(int) response.StatusCode}: {errorContent}");
		}
	}
}
