using System.Globalization;
using System.Text.RegularExpressions;

namespace UvcInspector.Core;

/// <summary>Count original video packet bytes and derive the observed media interval.</summary>
public sealed partial class CaptureAnalyzer
{
    private readonly object gate = new();
    private readonly List<(long Pts, long Duration)> timestamps = [];
    private readonly Queue<string> log = new();
    private long bytes, packets, frames, malformed;
    private double? timeBase, progressSeconds;
    private StreamInfo stream = StreamInfo.Unknown;
    private bool outputStarted;

    public void ReadPacketLine(string line)
    {
        lock (gate)
        {
            var tb = TimeBasePattern().Match(line);
            if (tb.Success)
            {
                double denominator = double.Parse(tb.Groups[2].Value, CultureInfo.InvariantCulture);
                if (denominator > 0) timeBase = double.Parse(tb.Groups[1].Value, CultureInfo.InvariantCulture) / denominator;
                return;
            }
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) return;
            var fields = line.Split(',');
            if (fields.Length < 6 || !long.TryParse(fields[0].Trim(), out long index) || index != 0
                || !long.TryParse(fields[2].Trim(), out long pts) || !long.TryParse(fields[3].Trim(), out long duration)
                || !long.TryParse(fields[4].Trim(), out long size) || size < 0 || duration < 0)
            { malformed++; return; }
            bytes = checked(bytes + size);
            packets++;
            // AV_NOPTS_VALUE is not a valid sample time. Missing timestamps stay explicit.
            if (pts != long.MinValue && timestamps.Count < 100_000) timestamps.Add((pts, duration));
        }
    }

    public void ReadDiagnosticLine(string line)
    {
        lock (gate)
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var count))
                frames = Math.Max(frames, count);
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), out long microseconds) && microseconds > 0)
                progressSeconds = microseconds / 1_000_000.0;
            if (line.StartsWith("Output #", StringComparison.Ordinal)) outputStarted = true;
            if (!outputStarted && stream == StreamInfo.Unknown)
            {
                var parsed = ParseStream(line);
                if (parsed is not null) stream = parsed;
            }
            // Keep diagnostics bounded and avoid filling the UI with progress key/value records.
            if (!ProgressLinePattern().IsMatch(line))
            {
                if (log.Count == 300) log.Dequeue();
                log.Enqueue(line);
            }
        }
    }

    public static StreamInfo? ParseStream(string line)
    {
        var video = VideoPattern().Match(line);
        if (!video.Success) return null;
        var size = SizePattern().Match(line);
        // The codec's FourCC can resemble a pixel format (e.g. YUY2). Prefer the
        // actual format after the codec descriptor rather than its earlier tag.
        var pixel = PixelPattern().Matches(line).LastOrDefault();
        var fps = FpsPattern().Match(line);
        var format = pixel?.Groups[1].Value.ToLowerInvariant() ?? "未知";
        var info = PixelFormats.Describe(format);
        return new(video.Groups[1].Value.ToLowerInvariant(), format,
            size.Success ? int.Parse(size.Groups[1].Value) : 0,
            size.Success ? int.Parse(size.Groups[2].Value) : 0,
            fps.Success ? double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            info.Chroma, info.Depth, line.Trim());
    }

    public MeasurementResult Result(CaptureRequest request, double wallSeconds, CaptureStatus status, int? exitCode = null, string? message = null)
    {
        lock (gate)
        {
            var warnings = new List<string>();
            var (seconds, source) = MediaInterval();
            if (source == "时间戳间隔估算（末包时长缺失）") warnings.Add("部分视频包没有时长，媒体区间末端由相邻时间戳间隔估算。");
            if (source == "FFmpeg 媒体进度") warnings.Add("包时间戳不足，使用 FFmpeg 媒体进度；没有用请求时长代替实测时长。");
            if (malformed > 0) warnings.Add($"有 {malformed} 条无法解析的包记录，统计不完整。");
            if (stream.PixelFormat == "未知") warnings.Add("未能识别实际像素格式，色度与位深保持未知。");
            if (stream.Width > 0 && (request.Width != stream.Width || request.Height != stream.Height))
                warnings.Add($"实际尺寸 {stream.Width}×{stream.Height} 与请求 {request.Width}×{request.Height} 不同。");
            if (request.Kind == "vcodec" && NormalizeCodec(request.Format) != NormalizeCodec(stream.Codec))
                warnings.Add("实际视频编码与所选模式不一致。");
            if (request.Kind == "pixel_format" && NormalizePixel(request.Format) != NormalizePixel(stream.PixelFormat))
                warnings.Add("实际像素格式与所选模式不一致。");
            if (status == CaptureStatus.Completed)
            {
                if (exitCode != 0 || packets == 0 || frames == 0 || bytes == 0 || seconds is not > 0 || malformed > 0)
                { status = CaptureStatus.Failed; message = "采集没有完整有效的数据，请检查设备占用、格式与诊断记录。"; }
                else if (request.Width != stream.Width || request.Height != stream.Height
                    || (request.Kind == "vcodec" && NormalizeCodec(request.Format) != NormalizeCodec(stream.Codec))
                    || (request.Kind == "pixel_format" && NormalizePixel(request.Format) != NormalizePixel(stream.PixelFormat)))
                { status = CaptureStatus.Failed; message = "采集输出与所选模式不一致，结果仅供诊断。"; }
                else if (seconds.Value < request.Seconds - Math.Max(.5, request.Seconds * .1))
                { status = CaptureStatus.Failed; message = "采集提前结束，未达到所选测试时长。"; }
            }
            double? actualFps = seconds is > 0 && frames > 0 ? frames / seconds.Value : null;
            if (actualFps.HasValue && actualFps.Value < request.Fps * .95)
                warnings.Add("采集帧率低于请求值的 95%；这不等于已证明 USB 硬件掉帧。");
            if (frames != packets && packets > 0 && frames > 0)
                warnings.Add("FFmpeg 帧计数与包计数不同；压缩包未解码，二者独立展示。");
            return new(status, stream, packets, frames, bytes, seconds, source, wallSeconds,
                seconds is > 0 ? bytes * 8 / seconds.Value / 1_000_000 : null,
                actualFps, PixelFormats.ReferenceMbps(stream),
                message ?? (status == CaptureStatus.Completed ? "测试完成，视频有效数据已统计。" : "正在采集视频数据…"),
                warnings, log.ToArray(), exitCode);
        }
    }

    private (double? Seconds, string Source) MediaInterval()
    {
        if (timeBase is > 0 && timestamps.Count >= 2)
        {
            var first = timestamps.Min(x => x.Pts);
            var last = timestamps.Max(x => x.Pts);
            double end = timestamps.Max(x => (double)x.Pts + x.Duration);
            string source = "视频包时间戳＋包时长";
            if (timestamps.Any(x => x.Duration == 0))
            {
                var ordered = timestamps.Select(x => x.Pts).Distinct().Order().ToArray();
                var gaps = ordered.Zip(ordered.Skip(1), (a, b) => (double)b - a).Where(x => x > 0).Order().ToArray();
                if (gaps.Length > 0) end = Math.Max(end, last + gaps[gaps.Length / 2]);
                source = "时间戳间隔估算（末包时长缺失）";
            }
            double seconds = (end - first) * timeBase.Value;
            if (seconds > 0 && double.IsFinite(seconds)) return (seconds, source);
        }
        return progressSeconds is > 0 ? (progressSeconds, "FFmpeg 媒体进度") : (null, "尚无有效媒体区间");
    }

    private static string NormalizeCodec(string value) => value.ToLowerInvariant() switch { "mjpg" or "mjpe" => "mjpeg", "h265" => "hevc", _ => value.ToLowerInvariant() };
    private static string NormalizePixel(string value) => value.ToLowerInvariant() switch { "yuy2" => "yuyv422", "uyvy" => "uyvy422", _ => value.ToLowerInvariant() };
    [GeneratedRegex(@"^#tb 0:\s*(\d+)/(\d+)")]
    private static partial Regex TimeBasePattern();
    [GeneratedRegex(@"Stream #\d+:\d+(?:\[[^\]]*\])?(?:\([^)]*\))?: Video:\s*([^\s,(]+)")]
    private static partial Regex VideoPattern();
    [GeneratedRegex(@"(?<!\d)(\d{2,5})x(\d{2,5})(?!\d)")]
    private static partial Regex SizePattern();
    [GeneratedRegex(@"(?<![a-z0-9_])(yuyv422|yuy2|uyvy422|uyvy|nv12|nv21|p0(?:10|16)(?:le|be)|yuva?j?(?:420|422|444)p(?:\d+(?:le|be))?|gbr(?:a)?p(?:\d+(?:le|be))?|(?:rgb|bgr)(?:24|48(?:le|be))|rgba|bgra|argb|abgr|rgb0|bgr0|0rgb|0bgr|gray)(?![a-z0-9_])", RegexOptions.IgnoreCase)]
    private static partial Regex PixelPattern();
    [GeneratedRegex(@"([0-9.]+) fps")]
    private static partial Regex FpsPattern();
    [GeneratedRegex(@"^(frame|fps|stream_\d+_\d+_q|bitrate|total_size|out_time_us|out_time_ms|out_time|dup_frames|drop_frames|speed|progress)=")]
    private static partial Regex ProgressLinePattern();
}
