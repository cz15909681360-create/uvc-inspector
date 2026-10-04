using System.Collections.Concurrent;

namespace UvcInspector.Core;

public static class NativePreview
{
    public static async Task RunAsync(string executable, CaptureRequest request, Action<byte[]> receive, CancellationToken cancellation)
    {
        var arguments = FfmpegEngine.CaptureInput(request);
        arguments.AddRange(["-map", "0:v:0", "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease", "-r", "12",
            "-c:v", "mjpeg", "-q:v", "5", "-threads", "1", "-f", "image2pipe", "pipe:1"]);
        using var process = ProcessRunner.Create(executable, arguments);
        if (!process.Start()) throw new InvalidOperationException("预览进程启动失败。");
        process.StandardInput.Close();
        var diagnostics = new ConcurrentQueue<string>();
        var stderr = ReadErrorsAsync();
        using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stall.Token);
        using var registration = linked.Token.Register(() => ProcessRunner.Kill(process));
        long count = 0;
        try
        {
            await JpegFrameReader.ReadAsync(process.StandardOutput.BaseStream, bytes =>
            {
                count++;
                stall.CancelAfter(TimeSpan.FromSeconds(15));
                receive(bytes);
            }, linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || count == 0) throw new InvalidOperationException("预览失败。\n" + string.Join('\n', diagnostics.TakeLast(8)));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("预览 15 秒没有收到画面，请检查设备占用或连接。"); }
        finally
        {
            ProcessRunner.Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }

        async Task ReadErrorsAsync()
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                diagnostics.Enqueue(line);
                if (diagnostics.Count > 80) diagnostics.TryDequeue(out _);
            }
        }
    }
}
