using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using UvcInspector.Core;

namespace UvcInspector;

internal static class EngineChecks
{
    public static async Task<bool> RunAsync(string report, string fixtures)
    {
        var records = new List<object>();
        bool healthy = false;
        string? error = null;
        try
        {
            void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            Require(BundledEngine.Available, "The published application has no embedded FFmpeg.");
            string cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "engine-check-cache");
            var paths = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => BundledEngine.ExtractForChecksAsync(cache, CancellationToken.None)));
            Require(paths.Distinct().Count() == 1, "Concurrent extraction did not reuse one cache entry.");
            await File.WriteAllTextAsync(paths[0], "Deliberately corrupt the isolated test cache.");
            string executable = await BundledEngine.ExtractForChecksAsync(cache, CancellationToken.None);
            var engine = new FfmpegEngine(executable);
            var info = await engine.InspectAsync(CancellationToken.None);
            Require(info.DirectShowAvailable, "Bundled engine lacks DirectShow.");
            records.Add(new { Check = "embedded extraction, concurrency, corruption repair and DirectShow", info.Version });
            var device = new VideoDevice("Synthetic fixture", "fixture", 0);
            foreach (var sample in new[] { ("rawvideo.nut", "pixel_format", "yuv420p", "rawvideo"), ("mjpeg.mjpg", "vcodec", "mjpeg", "mjpeg"),
                ("h264.h264", "vcodec", "h264", "h264"), ("hevc.hevc", "vcodec", "hevc", "hevc") })
            {
                var input = new List<string> { "-hide_banner", "-nostdin" };
                if (sample.Item4 == "mjpeg") input.AddRange(["-f", "mjpeg", "-framerate", "30"]);
                input.AddRange(["-i", Path.GetFullPath(Path.Combine(fixtures, sample.Item1))]);
                var request = new CaptureRequest(device, sample.Item2, sample.Item3, 320, 180, 30, 2);
                var result = await engine.MeasureInputAsync(request, input, null, CancellationToken.None);
                records.Add(new { Check = sample.Item4 + " streamcopy", Result = result });
                Require(result.Success && result.Stream.Codec == sample.Item4 && Math.Abs((result.MediaSeconds ?? 0) - 2) < .08
                    && Math.Abs((result.ActualFps ?? 0) - 30) < .6 && result.VideoBytes > 0, sample.Item4 + " measurement failed.");
                if (sample.Item4 == "rawvideo") Require(result.VideoBytes == 320L * 180 * 3 / 2 * 60, "Incorrect raw video bytes.");
                using var process = ProcessRunner.Create(executable, input.Concat(new[] { "-an", "-vf", "scale=160:90", "-frames:v", "1",
                    "-c:v", "mjpeg", "-threads", "1", "-f", "image2pipe", "pipe:1" }).ToArray());
                process.Start(); process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var stop = deadline.Token.Register(() => ProcessRunner.Kill(process));
                var diagnostic = process.StandardError.ReadToEndAsync();
                int decoded = 0;
                await JpegFrameReader.ReadAsync(process.StandardOutput.BaseStream, bytes =>
                {
                    using var stream = new MemoryStream(bytes);
                    var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    if (image.PixelWidth == 160 && image.PixelHeight == 90) decoded++;
                }, deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                string log = await diagnostic;
                Require(process.ExitCode == 0 && decoded == 1, sample.Item4 + " native preview decode failed: " + log);
                records.Add(new { Check = sample.Item4 + " JPEG preview decode", DecodedFrames = decoded });
            }
            using var cancellation = new CancellationTokenSource(500);
            var cancelled = await engine.MeasureInputAsync(new(device, "pixel_format", "yuv420p", 320, 180, 30, 10),
                ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30"], null, cancellation.Token);
            Require(cancelled.Status == CaptureStatus.Cancelled, "Bundled engine cancellation failed.");
            records.Add(new { Check = "process cancellation", Status = cancelled.Status });
            healthy = true;
        }
        catch (Exception e) { error = e.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Healthy = healthy, Error = error, Checks = records }, new JsonSerializerOptions { WriteIndented = true }));
        return healthy;
    }
}
