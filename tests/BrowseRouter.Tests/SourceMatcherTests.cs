using BrowseRouter.Core.Config;

namespace BrowseRouter.Tests;

public class SourceMatcherTests
{
    [Theory]
    [InlineData("TEAMS.EXE", "TEAMS.EXE", true)]
    [InlineData("TEAMS.EXE", "teams.exe", true)] // case-insensitive
    [InlineData("TEAMS.EXE", "TEAMSX.EXE", false)]
    [InlineData("TEAMS.EXE", null, false)]
    public void Process_matches_filename_case_insensitive(string value, string? processName, bool expected)
    {
        var m = new ProcessMatch { Value = value };
        Assert.Equal(expected, m.IsMatch(processName, processPath: null, windowTitle: null));
    }

    [Fact]
    public void ProcessPath_matches_full_path_only()
    {
        var m = new ProcessPathMatch { Value = @"C:\Program Files\X\x.exe" };
        Assert.True(m.IsMatch(null, @"c:\program files\x\x.exe", null));
        Assert.False(m.IsMatch(null, @"C:\Program Files\X\y.exe", null));
        Assert.False(m.IsMatch(null, null, null));
    }

    [Fact]
    public void ProcessPathPrefix_matches_starting_segment()
    {
        var m = new ProcessPathPrefixMatch { Value = @"C:\Tools\" };
        Assert.True(m.IsMatch(null, @"c:\tools\app.exe", null));
        Assert.True(m.IsMatch(null, @"C:\Tools\Sub\app.exe", null));
        Assert.False(m.IsMatch(null, @"C:\Other\app.exe", null));
    }

    [Theory]
    [InlineData("Outlook", "Inbox - Outlook", true)]
    [InlineData("outlook", "Inbox - Outlook", true)]
    [InlineData("Outlook", "Visual Studio", false)]
    [InlineData("Outlook", null, false)]
    public void WindowTitleContains_substring_case_insensitive(string needle, string? title, bool expected)
    {
        var m = new WindowTitleContainsMatch { Value = needle };
        Assert.Equal(expected, m.IsMatch(processName: null, processPath: null, windowTitle: title));
    }

    [Fact]
    public void WindowTitleRegex_matches()
    {
        var m = new WindowTitleRegexMatch { Value = @"^Inbox - (.+)$" };
        Assert.True(m.IsMatch(null, null, "Inbox - Outlook"));
        Assert.False(m.IsMatch(null, null, "Outlook Inbox"));
    }
}