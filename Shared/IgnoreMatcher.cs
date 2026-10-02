namespace FastCICD.Shared;

/// <summary>
/// Single definition of "is this relative path ignored". It is compiled into both the client and the
/// server so the two can never disagree about which files a mirror deployment may delete.
/// </summary>
internal static class IgnoreMatcher
{
	public static bool IsIgnored(string relativePath, IEnumerable<string> ignoredPatterns)
	{
		var path = relativePath.Replace('/', '\\');
		foreach (var pattern in ignoredPatterns)
		{
			// Trim both ends so "logs/", "\logs" and "logs" all mean the same thing.
			var ignored = pattern.Replace('/', '\\').Trim('\\');
			if (ignored.Length == 0)
				continue;

			if (path.Equals(ignored, StringComparison.OrdinalIgnoreCase) ||
				path.StartsWith(ignored + "\\", StringComparison.OrdinalIgnoreCase) ||
				path.Contains("\\" + ignored + "\\", StringComparison.OrdinalIgnoreCase) ||
				path.EndsWith("\\" + ignored, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}
}
