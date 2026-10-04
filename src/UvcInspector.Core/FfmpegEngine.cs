using System.Diagnostics;

namespace UvcInspector.Core;

public sealed class FfmpegEngine(string executable)
{
    public string Executable { get; } = executable;

    public static string? FindExecutable(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)) return Path.GetFullPath(preferred);
        foreach (var local in new[] { Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") })
            if (File.Exists(local)) return local;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var file = Path.Combine(folder.Trim('"'), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(file)) return file;
        }
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
            foreach (var directory in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg_*"))
                foreach (var file in Directory.EnumerateFiles(directory, "ffmpeg.exe", SearchOption.AllDirectories)) return file;
        return null;
    }

    public async Task<EngineInfo> InspectAsync(CancellationToken cancellation)
    {
        var version = await ProcessRunner.RunAsync(Executable, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10), cancellation);
        var devices = await ProcessRunner.RunAsync(Executable, ["-hide_banner", "-devices"], TimeSpan.FromSeconds(10), cancellation);
        if (version.ExitCode != 0 || devices.ExitCode != 0) throw new InvalidOperationException("所选 FFmpeg 不能正常运行。");
        bool dshow = devices.Stdout.Concat(devices.Stderr).Any(x => x.Contains("dshow", StringComparison.Ordinal));
        return new(Executable, version.Stdout.FirstOrDefault() ?? "未知版本", dshow);
    }

    public async Task<IReadOnlyList<VideoDevice>> DevicesAsync(CancellationToken cancellation)
    {
        var result = await ProcessRunner.RunAsync(Executable, ["-hide_banner", "-nostdin", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], TimeSpan.FromSeconds(15), cancellation);
        var lines = result.Stdout.Concat(result.Stderr).ToArray();
        var devices = DeviceParser.ParseDevices(lines);
        // DirectShow listing intentionally exits with an error after listing. Empty enumeration
        // is valid only if the device header/no-device message identifies the listing operation.
        if (devices.Count == 0 && !lines.Any(x => x.Contains("DirectShow", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Could not enumerate video devices", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("设备枚举失败。\n" + string.Join('\n', lines.TakeLast(8)));
        return devices;
    }

    public async Task<IReadOnlyList<CaptureMode>> ModesAsync(VideoDevice device, CancellationToken cancellation)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-f", "dshow", "-list_options", "true" };
        AddDevice(arguments, device);
        var result = await ProcessRunner.RunAsync(Executable, arguments, TimeSpan.FromSeconds(15), cancellation);
        var modes = DeviceParser.ParseModes(result.Stdout.Concat(result.Stderr));
        if (modes.Count == 0) throw new InvalidOperationException("没有读到设备支持模式。请关闭占用相机的应用后刷新。\n" + string.Join('\n', result.Stderr.TakeLast(8)));
        return modes;
    }

    public static List<string> CaptureInput(CaptureRequest request)
    {
        request.Validate();
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-rtbufsize", "256M", "-f", "dshow",
            "-video_size", $"{request.Width}x{request.Height}", "-framerate", request.Fps.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
            request.Kind == "vcodec" ? "-vcodec" : "-pixel_format", request.Format };
        AddDevice(arguments, request.Device);
        return arguments;
    }

    private static void AddDevice(List<string> args, VideoDevice device)
    {
        if (device.Identifier == device.Name && device.NameIndex > 0) args.AddRange(["-video_device_number", device.NameIndex.ToString()]);
        args.AddRange(["-i", "video=" + device.Identifier]);
    }

    public Task<MeasurementResult> MeasureAsync(CaptureRequest request, IProgress<MeasurementResult>? progress, CancellationToken cancellation)
        => MeasureInputAsync(request, CaptureInput(request), progress, cancellation);

    /// <summary>Accept an explicit input for deterministic integration checks; the GUI uses DirectShow.</summary>
    public async Task<MeasurementResult> MeasureInputAsync(CaptureRequest request, IReadOnlyList<string> input,
        IProgress<MeasurementResult>? progress, CancellationToken cancellation)
    {
        request.Validate();
        var arguments = input.Concat(new[] { "-t", request.Seconds.ToString(), "-map", "0:v:0", "-an", "-c:v", "copy",
            "-progress", "pipe:2", "-nostats", "-f", "framecrc", "pipe:1" }).ToArray();
        var analyzer = new CaptureAnalyzer();
        var timer = Stopwatch.StartNew();
        using var monitorStop = new CancellationTokenSource();
        var monitor = ReportProgressAsync();
        MeasurementResult result;
        try
        {
            var output = await ProcessRunner.RunAsync(Executable, arguments, TimeSpan.FromSeconds(request.Seconds + 25), cancellation,
                analyzer.ReadPacketLine, analyzer.ReadDiagnosticLine).ConfigureAwait(false);
            result = analyzer.Result(request, timer.Elapsed.TotalSeconds, output.ExitCode == 0 ? CaptureStatus.Completed : CaptureStatus.Failed,
                output.ExitCode, output.ExitCode == 0 ? null : "FFmpeg 采集失败，已保留部分结果与诊断记录。");
        }
        catch (OperationCanceledException)
        { result = analyzer.Result(request, timer.Elapsed.TotalSeconds, CaptureStatus.Cancelled, message: "测试已取消，设备已释放。"); }
        catch (TimeoutException e)
        { result = analyzer.Result(request, timer.Elapsed.TotalSeconds, CaptureStatus.TimedOut, message: e.Message); }
        finally
        {
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }
        return result;

        async Task ReportProgressAsync()
        {
            try
            {
                using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
                while (await tick.WaitForNextTickAsync(monitorStop.Token).ConfigureAwait(false))
                    progress?.Report(analyzer.Result(request, timer.Elapsed.TotalSeconds, CaptureStatus.Running));
            }
            catch (OperationCanceledException) { }
        }
    }
}
