namespace UvcInspector.Core;

public sealed record VideoDevice(string Name, string Identifier, int NameIndex)
{
    public string Label => NameIndex == 0 ? Name : $"{Name} · 同名设备 {NameIndex + 1}";
}

/// <summary>A driver's advertised range. Its endpoints need not describe every valid combination.</summary>
public sealed record CaptureMode(string Kind, string Format, int MinWidth, int MinHeight,
    int MaxWidth, int MaxHeight, double MinFps, double MaxFps)
{
    public string Encoding => Kind == "vcodec" ? Format.ToUpperInvariant() : "未压缩";
    public string PixelLabel => Kind == "pixel_format" ? Format.ToUpperInvariant() : "待采集识别";
    public string SizeLabel => MinWidth == MaxWidth && MinHeight == MaxHeight
        ? $"{MinWidth} × {MinHeight}" : $"{MinWidth} × {MinHeight} → {MaxWidth} × {MaxHeight}";
    public string FpsLabel => Math.Abs(MaxFps - MinFps) < .001 ? $"{MaxFps:0.###} fps" : $"{MinFps:0.###}–{MaxFps:0.###} fps";
    public bool IsRange => MinWidth != MaxWidth || MinHeight != MaxHeight || Math.Abs(MaxFps - MinFps) > .001;
    public string RangeNote => IsRange ? "驱动声明范围；所选组合仍需实际验证" : "驱动声明固定模式";
}

public sealed record CaptureRequest(VideoDevice Device, string Kind, string Format,
    int Width, int Height, double Fps, int Seconds)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Device.Identifier) || Device.Identifier.Length > 2048 || Device.Identifier.Any(char.IsControl))
            throw new ArgumentException("请选择有效的视频设备。");
        if (Kind is not ("pixel_format" or "vcodec")) throw new ArgumentException("未知格式类型，请重新选择设备支持模式。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Format, "^[a-zA-Z0-9_]{1,40}$"))
            throw new ArgumentException("格式名称无效，请使用设备列表中声明的格式。");
        if (Width is < 16 or > 8192 || Height is < 16 or > 8192)
            throw new ArgumentException("宽度、高度应在 16–8192 之间。");
        if (!double.IsFinite(Fps) || Fps is < 1 or > 240) throw new ArgumentException("帧率应在 1–240 fps 之间。");
        if (Seconds is < 2 or > 120) throw new ArgumentException("测试时长应在 2–120 秒之间。");
        if (Kind == "pixel_format" && PixelFormats.RequiresEvenSize(Format) && (Width % 2 != 0 || Height % 2 != 0))
            throw new ArgumentException("当前色度采样格式需要偶数宽度和高度。");
    }
}

public sealed record StreamInfo(string Codec, string PixelFormat, int Width, int Height, double? NominalFps,
    string Chroma, string BitDepth, string SourceLine)
{
    public static StreamInfo Unknown { get; } = new("未知", "未知", 0, 0, null, "未知", "未知", "");
    public string CodecLabel => Codec switch { "rawvideo" => "未压缩 / RAW", "mjpeg" => "MJPEG", "h264" => "H.264", "hevc" => "H.265 / HEVC", _ => Codec.ToUpperInvariant() };
}

public enum CaptureStatus { Running, Completed, Failed, Cancelled, TimedOut }

public sealed record MeasurementResult(CaptureStatus Status, StreamInfo Stream, long Packets, long Frames,
    long VideoBytes, double? MediaSeconds, string TimingSource, double WallSeconds,
    double? Mbps, double? ActualFps, double? ReferenceMbps, string Message,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Log, int? ExitCode)
{
    public bool Success => Status == CaptureStatus.Completed;
}

public sealed record EngineInfo(string Path, string Version, bool DirectShowAvailable);
public sealed record CommandOutput(int ExitCode, IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr);
