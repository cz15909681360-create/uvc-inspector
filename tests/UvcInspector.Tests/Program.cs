using System.Globalization;
using System.Text;
using System.Text.Json;
using UvcInspector.Core;

int checks = 0;
var records = new List<object>();
var request = new CaptureRequest(new("Fixture", "fixture", 0), "vcodec", "h264", 1920, 1080, 30, 5);
void Check(string name, bool pass)
{
    if (!pass) throw new Exception("FAIL: " + name);
    checks++; Console.WriteLine("PASS " + name);
}
void Near(string name, double? actual, double expected, double tolerance = 0.000001)
    => Check(name, actual.HasValue && Math.Abs(actual.Value - expected) < tolerance);
void Reject(string name, CaptureRequest sample)
{
    try { sample.Validate(); throw new Exception("Validation accepted " + name); }
    catch (ArgumentException) { Check(name, true); }
}

var devices = DeviceParser.ParseDevices(["[dshow] \"Camera\" (video)", "[dshow] Alternative name \"@device_one\"", "[dshow] \"Camera\" (video)",
    "[dshow] Alternative name \"@device_two\"", "[dshow] \"Microphone\" (audio)", "[dshow] Alternative name \"audio\""]);
Check("same-name cameras retain unique alternatives", devices.Count == 2 && devices[0].Identifier == "@device_one" && devices[1].Identifier == "@device_two");
Check("duplicate name index preserved", devices[1].NameIndex == 1);
var modes = DeviceParser.ParseModes(["pixel_format=nv12 min s=640x480 fps=5 max s=1920x1080 fps=59.94", "vcodec=mjpeg min s=1280x720 fps=30 max s=1280x720 fps=30", "pixel_format=nv12 min s=640x480 fps=5 max s=1920x1080 fps=59.94"]);
Check("mode ranges preserve both endpoints and deduplicate", modes.Count == 2 && modes[0].MaxWidth == 1920 && modes[0].MaxHeight == 1080 && modes[0].MinFps == 5 && modes[0].MaxFps == 59.94);
Reject("invalid zero dimensions", request with { Width = 0 });
Reject("oversized dimensions", request with { Height = 10000 });
Reject("non-finite FPS", request with { Fps = double.NaN });
Reject("infinite FPS", request with { Fps = double.PositiveInfinity });
Reject("zero FPS", request with { Fps = 0 });
Reject("too-short duration", request with { Seconds = 1 });
Reject("too-long duration", request with { Seconds = 121 });
Reject("invalid format name", request with { Format = "h264 -y" });
Reject("invalid kind", request with { Kind = "unknown" });
Reject("control characters in device", request with { Device = new("camera", "camera\n", 0) });
Reject("odd subsampled size", request with { Kind = "pixel_format", Format = "nv12", Width = 1919 });
Check("NV12 frame byte count", PixelFormats.FrameBytes("nv12", 1920, 1080) == 3_110_400);
Check("P010 stored in 16-bit words", PixelFormats.FrameBytes("p010le", 1920, 1080) == 6_220_800);
Check("planar 10-bit storage", PixelFormats.FrameBytes("yuv420p10le", 1920, 1080) == 6_220_800);
Check("YUYV frame byte count", PixelFormats.FrameBytes("yuyv422", 1920, 1080) == 4_147_200);
Check("GBRP 16-bit planes", PixelFormats.FrameBytes("gbrp10le", 1920, 1080) == 12_441_600);
Check("packed unknown stays unknown", PixelFormats.FrameBytes("v210", 1920, 1080) is null);
Check("P010 sample depth independent of word size", PixelFormats.Describe("p010le").Depth == "10-bit");
Check("planar 10-bit depth", PixelFormats.Describe("yuv420p10le").Depth == "10-bit");
var h264 = CaptureAnalyzer.ParseStream("Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, 29.97 fps")!;
Check("codec separate from pixel format", h264.Codec == "h264" && h264.PixelFormat == "yuv420p");
Check("compressed reference stays unavailable", PixelFormats.ReferenceMbps(h264) is null);
Near("NV12 reference Mbps", PixelFormats.ReferenceMbps(new("rawvideo", "nv12", 1920, 1080, 30, "4:2:0", "8-bit", "")), 746.496);
Check("HEVC 10-bit stream detection", CaptureAnalyzer.ParseStream("Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv), 1920x1080, 30 fps")!.BitDepth == "10-bit");
Check("actual pixel format preferred over codec FourCC", CaptureAnalyzer.ParseStream("Stream #0:0: Video: rawvideo (YUY2 / 0x32595559), yuyv422, 640x360, 30 fps")!.PixelFormat == "yuyv422");

CaptureAnalyzer Fixture(long offset = 0, bool durations = true)
{
    var analyzer = new CaptureAnalyzer();
    analyzer.ReadPacketLine("#tb 0: 1/30");
    analyzer.ReadDiagnosticLine("Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, 30 fps");
    for (int i = 0; i < 150; i++) analyzer.ReadPacketLine($"0, {offset + i}, {offset + i}, {(durations ? 1 : 0)}, 1000, 0x00000000");
    analyzer.ReadDiagnosticLine("bitrate=N/A");
    analyzer.ReadDiagnosticLine("frame=150");
    return analyzer;
}
var normal = Fixture().Result(request, 5.2, CaptureStatus.Completed, 0);
Check("completed requires valid data", normal.Success);
Near("exact payload byte bitrate independent of text bitrate", normal.Mbps, .24);
Near("FPS independent of N/A bitrate", normal.ActualFps, 30);
Check("bytes and packets distinct from mux size", normal.VideoBytes == 150000 && normal.Packets == 150 && normal.Frames == 150);
Near("timestamp origin does not inflate interval", Fixture(30000).Result(request, 5.2, CaptureStatus.Completed, 0).MediaSeconds, 5);
Check("nonzero exit cannot claim success", !Fixture().Result(request, 5.2, CaptureStatus.Completed, 1).Success);
Check("empty success code rejected", !new CaptureAnalyzer().Result(request, 5, CaptureStatus.Completed, 0).Success);
Check("cancellation retains partial result", Fixture().Result(request, 5, CaptureStatus.Cancelled).Status == CaptureStatus.Cancelled);
Check("timeout distinct from failure", Fixture().Result(request, 5, CaptureStatus.TimedOut).Status == CaptureStatus.TimedOut);
Check("missing packet durations explicitly estimated", Fixture(durations: false).Result(request, 5, CaptureStatus.Completed, 0).TimingSource.Contains("估算"));
Check("wrong dimensions rejected", !Fixture().Result(request with { Width = 1280 }, 5, CaptureStatus.Completed, 0).Success);
Check("wrong codec rejected", !Fixture().Result(request with { Format = "hevc" }, 5, CaptureStatus.Completed, 0).Success);
Check("requested duration never substituted", new CaptureAnalyzer().Result(request, 5, CaptureStatus.Failed, 1).MediaSeconds is null);
var truncated = Fixture(); truncated.ReadPacketLine("corrupt packet record");
Check("malformed packet record prevents success", !truncated.Result(request, 5, CaptureStatus.Completed, 0).Success);
Check("early EOF rejected", !Fixture().Result(request with { Seconds = 10 }, 5, CaptureStatus.Completed, 0).Success);
var arguments = FfmpegEngine.CaptureInput(request with { Device = new("Camera & whoami", "Camera & whoami", 1) });
Check("device arguments not shell concatenation", arguments.Contains("video=Camera & whoami") && arguments.Contains("-video_device_number"));

var jpeg = new byte[] { 1, 2, 0xff, 0xd8, 7, 8, 0xff, 0xd9, 0xff, 0xd8, 9, 0xff, 0xd9 };
var frames = new List<byte[]>();
await JpegFrameReader.ReadAsync(new MemoryStream(jpeg), frames.Add, CancellationToken.None);
Check("MJPEG framing discards preamble and separates images", frames.Count == 2 && frames[0].Length == 6 && frames[1].Length == 5);
try { await JpegFrameReader.ReadAsync(new MemoryStream([0xff, 0xd8, 1]), _ => { }, CancellationToken.None); throw new Exception("Truncated image accepted"); }
catch (InvalidDataException) { Check("incomplete preview image rejected", true); }

if (args.Contains("--integration"))
{
    string ffmpeg = FfmpegEngine.FindExecutable() ?? throw new Exception("FFmpeg missing for integration checks");
    var engine = new FfmpegEngine(ffmpeg);
    var info = await engine.InspectAsync(CancellationToken.None);
    Check("installed engine inspection", info.DirectShowAvailable);
    string output = Path.GetFullPath(Path.Combine("artifacts", "integration")); Directory.CreateDirectory(output);
    foreach (var sample in new[] { ("rawvideo", "pixel_format", "yuv420p", "-"), ("mjpeg", "vcodec", "mjpeg", ".mjpg"), ("libx264", "vcodec", "h264", ".h264"), ("libx265", "vcodec", "hevc", ".hevc") })
    {
        string file = Path.Combine(output, sample.Item1 + (sample.Item4 == "-" ? ".nut" : sample.Item4));
        var encodeArgs = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "2", "-an", "-c:v", sample.Item1 };
        if (sample.Item1 == "libx264") encodeArgs.AddRange(["-preset", "ultrafast", "-bf", "0"]);
        if (sample.Item1 == "libx265") encodeArgs.AddRange(["-preset", "ultrafast", "-x265-params", "bframes=0:log-level=error"]);
        if (sample.Item1 == "mjpeg") encodeArgs.AddRange(["-threads", "1"]);
        encodeArgs.Add(file);
        var encoded = await ProcessRunner.RunAsync(ffmpeg, encodeArgs, TimeSpan.FromSeconds(30), CancellationToken.None);
        Check(sample.Item1 + " fixture generation", encoded.ExitCode == 0);
        var input = new List<string> { "-hide_banner", "-nostdin" };
        if (sample.Item1 == "mjpeg") input.AddRange(["-f", "mjpeg", "-framerate", "30"]);
        input.AddRange(["-i", file]);
        var sampleRequest = request with { Kind = sample.Item2, Format = sample.Item3, Width = 320, Height = 180, Seconds = 2 };
        var measured = await engine.MeasureInputAsync(sampleRequest, input, null, CancellationToken.None);
        records.Add(new { Sample = sample.Item1, Result = measured });
        Check(sample.Item1 + " streamcopy measurement success", measured.Success);
        Near(sample.Item1 + " observed duration", measured.MediaSeconds, 2, .08);
        Near(sample.Item1 + " observed FPS", measured.ActualFps, 30, .6);
        Check(sample.Item1 + " separated codec metadata", measured.Stream.Codec == (sample.Item1 == "rawvideo" ? "rawvideo" : sample.Item3));
        if (sample.Item1 == "rawvideo") Check("exact raw frame bytes", measured.VideoBytes == 320L * 180 * 3 / 2 * 60);
    }
    using (var cancel = new CancellationTokenSource(450))
    {
        var longRequest = request with { Kind = "pixel_format", Format = "yuv420p", Width = 320, Height = 180, Seconds = 10 };
        var cancelled = await engine.MeasureInputAsync(longRequest, ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30"], null, cancel.Token);
        Check("active capture cancellation and process cleanup", cancelled.Status == CaptureStatus.Cancelled);
    }
    try
    {
        await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=16x16:rate=1", "-f", "null", "-"], TimeSpan.FromMilliseconds(350), CancellationToken.None);
        throw new Exception("Timeout was not enforced");
    }
    catch (TimeoutException) { Check("process timeout and cleanup", true); }
    using (var p = ProcessRunner.Create(ffmpeg, ["-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=32x32:rate=5", "-t", "1", "-c:v", "mjpeg", "-threads", "1", "-f", "image2pipe", "pipe:1"]))
    {
        p.Start(); p.StandardInput.Close();
        var errors = p.StandardError.ReadToEndAsync();
        int previewFrames = 0;
        await JpegFrameReader.ReadAsync(p.StandardOutput.BaseStream, _ => previewFrames++, CancellationToken.None);
        await p.WaitForExitAsync(); await errors;
        Check("real FFmpeg MJPEG preview stream", p.ExitCode == 0 && previewFrames == 5);
    }
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Checks = checks, Engine = info, Samples = records }, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Contains("--devices") || args.Contains("--hardware"))
    {
        var attached = await engine.DevicesAsync(CancellationToken.None);
        Console.WriteLine("Attached video sources: " + attached.Count);
        foreach (var device in attached)
        {
            Console.WriteLine("DEVICE " + device.Label);
            try { Console.WriteLine("MODES " + (await engine.ModesAsync(device, CancellationToken.None)).Count); }
            catch (Exception e) { Console.WriteLine("MODE CHECK: " + e.Message); }
        }
        if (args.Contains("--hardware"))
        {
            var physical = attached.FirstOrDefault(x => x.Name == "FHD Camera") ?? throw new Exception("Physical camera fixture unavailable");
            var available = await engine.ModesAsync(physical, CancellationToken.None);
            var selected = available.GroupBy(x => (x.Kind, x.Format)).Select(group => group.OrderBy(x => x.MinWidth * x.MinHeight).First()).Take(2).ToArray();
            Check("physical camera has advertised test modes", selected.Length > 0);
            Console.WriteLine("Physical advertised formats: " + string.Join(", ", available.Select(x => x.Format).Distinct()));
            foreach (var mode in selected)
            {
                var cameraRequest = new CaptureRequest(physical, mode.Kind, mode.Format, mode.MinWidth, mode.MinHeight,
                    mode.MinWidth == mode.MaxWidth && mode.MinHeight == mode.MaxHeight ? mode.MaxFps : mode.MinFps, 2);
                var actual = await engine.MeasureAsync(cameraRequest, null, CancellationToken.None);
                records.Add(new { Sample = "physical-" + mode.Format, Result = actual });
                Console.WriteLine(JsonSerializer.Serialize(new { Sample = mode.Format, actual.Status, actual.Stream, actual.MediaSeconds, actual.Mbps, actual.ActualFps, actual.Message, actual.Warnings }));
                Check("physical " + mode.Format + " capture", actual.Success);
                Check("physical " + mode.Format + " valid media time", actual.MediaSeconds is >= 1.5 and <= 2.5);
            }
            var previewMode = selected[0];
            var previewRequest = new CaptureRequest(physical, previewMode.Kind, previewMode.Format, previewMode.MinWidth, previewMode.MinHeight, previewMode.MaxFps, 2);
            using var previewCancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            int seen = 0;
            try
            {
                await NativePreview.RunAsync(ffmpeg, previewRequest, bytes => { if (++seen == 3) previewCancel.Cancel(); }, previewCancel.Token);
            }
            catch (OperationCanceledException) { }
            Check("physical native preview receives frames and releases camera", seen >= 3);
            File.WriteAllText(Path.Combine(output, "hardware-results.json"), JsonSerializer.Serialize(new { Checks = checks, Samples = records }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
Console.WriteLine($"RESULT: {checks} checks passed.");
