using ScribeWench.Core;

namespace ScribeWench.Tests;

public class PathHelperTests
{
    [Fact]
    public void Expand_TildePrefix_ReplacesWithHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = PathHelper.Expand("~/.scribewench/config.json");

        Assert.StartsWith(home, result);
        Assert.EndsWith("config.json", result);
        Assert.DoesNotContain("~", result);
    }

    [Fact]
    public void Expand_TildeOnly_ReturnsHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = PathHelper.Expand("~");

        Assert.Equal(Path.GetFullPath(home), result);
    }

    [Fact]
    public void Expand_AbsolutePath_ReturnsUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "test.txt");
        var result = PathHelper.Expand(path);

        Assert.Equal(Path.GetFullPath(path), result);
    }

    [Fact]
    public void Expand_NoTilde_ReturnsFullPath()
    {
        var result = PathHelper.Expand("relative/path");

        Assert.True(Path.IsPathRooted(result));
    }
}
