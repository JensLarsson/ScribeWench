namespace ScribeWench.CLI;

// The published exe unpacks itself under %TEMP% and runs from there. Windows clears out old temp
// files that nothing has open, and a bot left running for weeks would lose libraries it only loads
// when a session ends. Holding every unpacked file open keeps them.
public static class UnpackedFiles
{
    private static readonly List<FileStream> Held = [];

    public static void Hold()
    {
        if (!AppContext.BaseDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            try { Held.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
