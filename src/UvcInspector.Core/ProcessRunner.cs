using System.Diagnostics;
using System.Text;

namespace UvcInspector.Core;

public static class ProcessRunner
{
    public static Process Create(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }

    public static async Task<CommandOutput> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellation, Action<string>? stdoutLine = null, Action<string>? stderrLine = null)
    {
        using var process = Create(executable, arguments);
        if (!process.Start()) throw new InvalidOperationException("无法启动 FFmpeg。");
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var registration = linked.Token.Register(() => Kill(process));
        var stdout = ReadLinesAsync(process.StandardOutput, stdoutLine);
        var stderr = ReadLinesAsync(process.StandardError, stderrLine);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new(process.ExitCode, output, errors);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw new TimeoutException("FFmpeg 响应超时，已停止采集并释放设备。");
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Observe both readers even if a parser fails, to avoid orphaned tasks.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadLinesAsync(StreamReader reader, Action<string>? receive)
    {
        var lines = new Queue<string>();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? callbackFailure = null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Continue draining even if a consumer fails. Otherwise a full pipe can
            // deadlock the child before its exit/cleanup can be observed.
            if (callbackFailure is null)
            {
                try { receive?.Invoke(line); }
                catch (Exception e) { callbackFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            }
            if (lines.Count == 1000) lines.Dequeue();
            lines.Enqueue(line);
        }
        callbackFailure?.Throw();
        return lines.ToArray();
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
