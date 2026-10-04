using System.Text.RegularExpressions;

namespace UvcInspector.Core;

/// <summary>Describe samples separately from their storage layout; unknown layouts remain unknown.</summary>
public static partial class PixelFormats
{
    public static (string Chroma, string Depth) Describe(string format)
    {
        var name = format.ToLowerInvariant();
        if (name is "yuyv422" or "yuy2" or "uyvy422" or "uyvy") return ("4:2:2", "8-bit");
        if (name is "nv12" or "nv21") return ("4:2:0", "8-bit");
        if (name is "p010le" or "p010be") return ("4:2:0", "10-bit");
        if (name is "p016le" or "p016be") return ("4:2:0", "16-bit");
        var match = YuvPattern().Match(name);
        if (match.Success)
        {
            var c = match.Groups[1].Value;
            return ($"{c[0]}:{c[1]}:{c[2]}", $"{(match.Groups[2].Success ? match.Groups[2].Value : "8")}-bit");
        }
        if (name is "rgb24" or "bgr24" or "rgba" or "bgra" or "argb" or "abgr" or "rgb0" or "bgr0" or "0rgb" or "0bgr") return ("RGB 4:4:4", "8-bit");
        if (name is "rgb48le" or "rgb48be" or "bgr48le" or "bgr48be") return ("RGB 4:4:4", "16-bit");
        var rgb = RgbPlanarPattern().Match(name);
        if (rgb.Success) return ("RGB 4:4:4", $"{(rgb.Groups[1].Success ? rgb.Groups[1].Value : "8")}-bit");
        if (name == "gray") return ("单通道", "8-bit");
        return ("未知", "未知");
    }

    public static bool RequiresEvenSize(string format) => format.Contains("420", StringComparison.OrdinalIgnoreCase)
        || format.ToLowerInvariant() is "nv12" or "nv21" or "p010le" or "p010be" or "p016le" or "p016be";

    public static long? FrameBytes(string format, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        long pixels = (long)width * height;
        long chroma420 = 2L * ((width + 1) / 2) * ((height + 1) / 2);
        long chroma422 = 2L * ((width + 1) / 2) * height;
        var name = format.ToLowerInvariant();
        if (name is "yuyv422" or "yuy2" or "uyvy422" or "uyvy") return ((width + 1L) / 2) * 4 * height;
        if (name is "nv12" or "nv21") return pixels + chroma420;
        if (name is "p010le" or "p010be" or "p016le" or "p016be") return 2 * (pixels + chroma420);
        if (name is "rgb24" or "bgr24") return pixels * 3;
        if (name is "rgba" or "bgra" or "argb" or "abgr" or "rgb0" or "bgr0" or "0rgb" or "0bgr") return pixels * 4;
        if (name is "rgb48le" or "rgb48be" or "bgr48le" or "bgr48be") return pixels * 6;
        if (name == "gray") return pixels;
        var yuv = YuvPattern().Match(name);
        if (yuv.Success)
        {
            int depth = yuv.Groups[2].Success ? int.Parse(yuv.Groups[2].Value) : 8;
            long samples = pixels + (yuv.Groups[1].Value switch { "420" => chroma420, "422" => chroma422, _ => 2 * pixels });
            if (name.StartsWith("yuva", StringComparison.Ordinal)) samples += pixels;
            return samples * (depth > 8 ? 2 : 1);
        }
        var rgb = RgbPlanarPattern().Match(name);
        if (rgb.Success) return pixels * (name.StartsWith("gbrap", StringComparison.Ordinal) ? 4 : 3) * (rgb.Groups[1].Success ? 2 : 1);
        return null;
    }

    public static double? ReferenceMbps(StreamInfo stream)
    {
        // A decoded pixel format cannot predict the compressed packet size.
        if (stream.Codec != "rawvideo" || stream.NominalFps is not > 0) return null;
        var bytes = FrameBytes(stream.PixelFormat, stream.Width, stream.Height);
        return bytes.HasValue ? bytes.Value * 8 * stream.NominalFps.Value / 1_000_000 : null;
    }

    [GeneratedRegex(@"^yuva?j?(420|422|444)p(?:(9|10|12|14|16)(?:le|be))?$")]
    private static partial Regex YuvPattern();
    [GeneratedRegex(@"^gbr(?:a)?p(?:(9|10|12|14|16)(?:le|be))?$")]
    private static partial Regex RgbPlanarPattern();
}
