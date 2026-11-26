namespace ScribeWench.Core;

public static class PathHelper
{
    // "~" is the user's home folder.
    public static string Expand(string path)
    {
        if (path.StartsWith("~/") || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = home + path[1..];
        }
        return Path.GetFullPath(path);
    }
}
