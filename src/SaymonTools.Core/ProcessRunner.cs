using System.Diagnostics;
using System.Text;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core;

public static class ProcessRunner
{
    /// <summary>
    /// Runs a program without a window, streaming stderr lines to <paramref name="onStdErr"/>.
    /// Kills the process tree on cancellation and throws <see cref="ConversionException"/> on a non-zero exit.
    /// </summary>
    public static async Task<string> RunAsync(
        string exe,
        IEnumerable<string> args,
        CancellationToken ct,
        Action<string>? onStdErr = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderrTail = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stdout)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 15)
                    stderrTail.Dequeue();
            }
            onStdErr?.Invoke(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new ConversionException(T($"Cannot start {Path.GetFileName(exe)}: {ex.Message}", $"Не удалось запустить {Path.GetFileName(exe)}: {ex.Message}"), ex);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
        // Make sure the async readers drained.
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string tail;
            lock (stderrTail)
                tail = string.Join(Environment.NewLine, stderrTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(5));
            throw new ConversionException(
                T($"{Path.GetFileNameWithoutExtension(exe)} failed with exit code {process.ExitCode}.", $"{Path.GetFileNameWithoutExtension(exe)} завершился с кодом ошибки {process.ExitCode}.")
                + (tail.Length > 0 ? Environment.NewLine + tail : ""));
        }
        lock (stdout)
            return stdout.ToString();
    }
}
