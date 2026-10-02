using FastCICD.Shared;

namespace FastCICD.Tests;

public class IgnoreMatcherTests
{
	[Theory]
	[InlineData("logs", "logs")]
	[InlineData("logs", @"logs\app.log")]
	[InlineData("logs", @"sub\logs\app.log")]
	[InlineData("logs", @"a\b\logs")]
	[InlineData("logs/", @"logs\app.log")]
	[InlineData("/logs", @"sub\logs\app.log")]
	[InlineData(@"\logs\", @"logs\app.log")]
	[InlineData("LOGS", @"logs\app.log")]
	[InlineData("appsettings.Development.json", "appsettings.Development.json")]
	[InlineData("appsettings.Development.json", @"config\appsettings.Development.json")]
	[InlineData("sub/dir", @"sub\dir\file.txt")]
	[InlineData("logs", "sub/logs/app.log")]
	public void Matches(string pattern, string path)
		=> Assert.True(IgnoreMatcher.IsIgnored(path, [pattern]));

	[Theory]
	[InlineData("logs", @"logs2\app.log")]
	[InlineData("logs", @"mylogs\app.log")]
	[InlineData("log", @"logs\app.log")]
	[InlineData("app.log", @"myapp.log")]
	[InlineData("sub/dir", @"sub\other\dir\file.txt")]
	[InlineData("", @"logs\app.log")]
	[InlineData("/", @"logs\app.log")]
	public void DoesNotMatch(string pattern, string path)
		=> Assert.False(IgnoreMatcher.IsIgnored(path, [pattern]));

	[Fact]
	public void AnyPatternMayMatch()
		=> Assert.True(IgnoreMatcher.IsIgnored(@"bin\x.dll", ["logs", "bin"]));

	[Fact]
	public void NoPatternsIgnoresNothing()
		=> Assert.False(IgnoreMatcher.IsIgnored("anything.txt", []));
}
