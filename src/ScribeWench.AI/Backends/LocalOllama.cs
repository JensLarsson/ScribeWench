using System.Diagnostics;

namespace ScribeWench.AI.Backends;

// The Ollama program on this machine, as opposed to its API, which may be on another one.
public interface IOllamaProgram
{
    bool Installed { get; }

    /// <summary>Starts it in the background. False when it isn't installed or won't start.</summary>
    bool Start();
}

public sealed class LocalOllama : IOllamaProgram
{
    public bool Installed => FindExecutable() is not null;

    // It keeps running after this program ends, as it would had the user started it.
    public bool Start()
    {
        if (FindExecutable() is not { } exe) return false;
        try
        {
            // Through the shell, so the server gets a hidden console of its own. As a plain child it
            // would hold this program's output handles open, and whatever reads them would wait for
            // Ollama to end.
            using var process = Process.Start(new ProcessStartInfo(exe, "serve")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var candidates = new List<string>();

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(folder.Trim().Trim('"'), name));

        if (OperatingSystem.IsWindows())
        {
            // The installer's own folders: PATH only changes for windows opened after the install.
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", name));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", name));
        }
        else
        {
            candidates.AddRange(["/usr/local/bin/ollama", "/usr/bin/ollama", "/opt/homebrew/bin/ollama"]);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}
