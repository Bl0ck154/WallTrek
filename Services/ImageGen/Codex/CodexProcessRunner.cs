using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WallTrek.Services.ImageGen.Codex;

/// <summary>PATH-based resolution; never searches other Windows user profiles.</summary>
public static class CodexExecutableResolver
{
    public static string? Find(string command, string? explicitPath, bool allowCmd)
    {
        var filenames = allowCmd
            ? new[] { command + ".exe", command + ".cmd" }
            : new[] { command + ".exe" };
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            try
            {
                if (!Path.IsPathFullyQualified(explicitPath) || !File.Exists(explicitPath))
                    return null;
                var path = Path.GetFullPath(explicitPath);
                return filenames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    ? path : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var filename in filenames)
            {
                try
                {
                    var path = Path.GetFullPath(Path.Combine(directory.Trim('"'), filename));
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
        }
        return null;
    }
}

/// <summary>
/// Executes only resolved local commands. Never forwards credentials, custom environments,
/// prompt strings, or full output to logs. Exit diagnostics are classified in-memory.
/// </summary>
public sealed class CodexProcessRunner : ICodexCommandRunner
{
    public async Task<CodexCommandResult> RunAsync(
        string executable, IReadOnlyList<string> args, string workingDirectory,
        TimeSpan timeout, bool probeOutput, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // npm's Codex installation may resolve to codex.cmd. A cmd wrapper is
        // permitted ONLY for fixed, non-sensitive version/login-status probes.
        // The image helper must be a real executable and always uses ArgumentList.
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.GetFileName(executable).Equals("codex.cmd", StringComparison.OrdinalIgnoreCase) ||
                !(args.SequenceEqual(new[] { "--version" }) ||
                  args.SequenceEqual(new[] { "login", "status" })) ||
                executable.IndexOfAny(new[] { '"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n' }) >= 0 ||
                !OperatingSystem.IsWindows())
                throw new CodexImageException(CodexImageFailure.ToolUnavailable);
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("\"\"" + executable + "\" " + string.Join(" ", args) + "\"");
        }
        else
        {
            if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new CodexImageException(CodexImageFailure.ToolUnavailable);
            foreach (var arg in args) info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new CodexImageException(CodexImageFailure.ToolUnavailable);
        }
        catch (Win32Exception) { throw new CodexImageException(CodexImageFailure.ToolUnavailable); }
        catch (InvalidOperationException) { throw new CodexImageException(CodexImageFailure.ToolUnavailable); }

        var classifier = new FailureClassifier();
        var stdout = ConsumeAsync(process.StandardOutput, classifier, probeOutput);
        var stderr = ConsumeAsync(process.StandardError, classifier, probeOutput);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            var fragments = await Task.WhenAll(stdout, stderr);
            var probe = probeOutput ? string.Concat(fragments).Length > 1024
                ? string.Concat(fragments).Substring(0, 1024)
                : string.Concat(fragments) : "";
            return new CodexCommandResult(process.ExitCode, probe,
                process.ExitCode == 0 ? null : classifier.Failure ?? CodexImageFailure.BackendChanged);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            throw new CodexImageException(cancellationToken.IsCancellationRequested
                ? CodexImageFailure.Cancelled : CodexImageFailure.Timeout);
        }
    }

    private static async Task<string> ConsumeAsync(StreamReader reader, FailureClassifier classifier, bool capture)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            classifier.Feed(new string(buffer, 0, read));
            if (capture && result.Length < 512)
                result.Append(buffer, 0, Math.Min(512 - result.Length, read));
        }
        return result.ToString();
    }

    private sealed class FailureClassifier
    {
        private string _tail = "";
        public CodexImageFailure? Failure { get; private set; }

        public void Feed(string fragment)
        {
            // Keep a short rolling window only; never retain stdout/stderr or user paths.
            var value = (_tail + fragment).ToLowerInvariant();
            if (value.Contains("429") || value.Contains("rate_limit") ||
                value.Contains("rate limit") || value.Contains("quota exceeded") ||
                value.Contains("too many requests"))
                Failure = CodexImageFailure.RateLimited;
            else if (value.Contains("401") || value.Contains("login required") ||
                value.Contains("not logged in") || value.Contains("token expired") ||
                value.Contains("unauthorized"))
                Failure ??= CodexImageFailure.NotSignedIn;
            else if (value.Contains("403") || value.Contains("not eligible") ||
                value.Contains("subscription") || value.Contains("keyring-only") ||
                value.Contains("file-based auth"))
                Failure ??= CodexImageFailure.UnsupportedAccount;
            else if (value.Contains("not available") || value.Contains("no image tool") ||
                value.Contains("not supported for this account"))
                Failure ??= CodexImageFailure.ToolUnavailable;
            _tail = value.Length > 160 ? value[^160..] : value;
        }
    }
}
