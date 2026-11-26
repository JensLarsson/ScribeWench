using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// What `claude auth status` reports.
public sealed record ClaudeAccount(bool LoggedIn, string? AuthMethod, string? Email, string? Plan)
{
    // Signed in with a Claude account (a subscription), not an API key.
    public bool UsesSubscription =>
        LoggedIn && AuthMethod is not null &&
        (AuthMethod == "claude.ai" || AuthMethod.Contains("oauth", StringComparison.OrdinalIgnoreCase));

    public string Describe() =>
        !LoggedIn ? "Claude Code isn't signed in. Run `claude` on this PC and sign in, or summaries will fail."
        : !UsesSubscription ? $"Claude Code is using {AuthMethod ?? "an unknown login"}, not a Claude subscription, so summaries are billed to that."
        : $"Summaries use the Claude account {Email ?? "(unknown email)"}, {Plan ?? "unknown"} plan.";
}

// The Claude Code program on this PC: is it there, who is it signed in as, and one prompt run through it.
public sealed class ClaudeCli
{
    // Null when the program can't be started.
    public async Task<string?> GetVersionAsync(string executablePath, CancellationToken ct = default)
    {
        try
        {
            var (exitCode, output) = await RunAsync(executablePath, ["--version"], TimeSpan.FromSeconds(20), ct);
            return exitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // Null when the program can't be run.
    public async Task<ClaudeAccount?> GetAccountAsync(string executablePath, CancellationToken ct = default)
    {
        try
        {
            // Not signed in still prints JSON (loggedIn: false), possibly with a non-zero exit code.
            var (_, output) = await RunAsync(executablePath, ["auth", "status", "--json"], TimeSpan.FromSeconds(30), ct);
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            string? Text(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var loggedIn = root.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            return new ClaudeAccount(loggedIn, Text("authMethod"), Text("email"), Text("subscriptionType"));
        }
        catch
        {
            return null;
        }
    }

    // One text-in, text-out call: tools, settings, MCP servers and skills are all switched off.
    // Returns what `--output-format json` printed.
    public async Task<string> PromptAsync(ClaudeCodeConfig config, string workDir, string systemPromptPath, string userMessage, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(config.ExecutablePath) ? "claude" : config.ExecutablePath,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
                 {
                     "-p",
                     "--model", config.Model,
                     "--effort", config.Effort,
                     "--system-prompt-file", systemPromptPath,
                     "--tools", "",
                     "--setting-sources", "",
                     "--strict-mcp-config",
                     "--disable-slash-commands",
                     "--no-session-persistence",
                     "--output-format", "json",
                 })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start '{start.FileName}'. Install Claude Code (https://code.claude.com) and run `claude` once to log in, " +
                "or set ai.claudeCode.executablePath in the config.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, config.TimeoutMinutes)));
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            // On stdin, not the command line, which has a length limit.
            await process.StandardInput.WriteAsync(userMessage.AsMemory(), timeout.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException(
                    $"Claude Code exited with code {process.ExitCode} and no output. {stderr.Trim()}");
            return stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Claude Code did not finish within {config.TimeoutMinutes} minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string executablePath, string[] arguments, TimeSpan limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(executablePath) ? "claude" : executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The program didn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, output);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
