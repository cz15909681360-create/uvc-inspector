using System.Globalization;
using System.Text.RegularExpressions;

namespace UvcInspector.Core;

public static partial class DeviceParser
{
    public static IReadOnlyList<VideoDevice> ParseDevices(IEnumerable<string> lines)
    {
        var devices = new List<VideoDevice>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        bool videoPending = false;
        foreach (var line in lines)
        {
            var device = DevicePattern().Match(line);
            if (device.Success)
            {
                var name = device.Groups[1].Value;
                counts.TryGetValue(name, out int index);
                counts[name] = index + 1;
                devices.Add(new(name, name, index));
                videoPending = true;
                continue;
            }
            var alternative = AlternativePattern().Match(line);
            if (videoPending && alternative.Success)
            {
                devices[^1] = devices[^1] with { Identifier = alternative.Groups[1].Value };
                videoPending = false;
            }
            if (line.Contains("(audio)", StringComparison.Ordinal)) videoPending = false;
        }
        return devices;
    }

    public static IReadOnlyList<CaptureMode> ParseModes(IEnumerable<string> lines)
    {
        var modes = new List<CaptureMode>();
        foreach (var line in lines)
        {
            var match = ModePattern().Match(line);
            if (!match.Success) continue;
            var mode = new CaptureMode(match.Groups[1].Value, match.Groups[2].Value.ToLowerInvariant(),
                int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value),
                int.Parse(match.Groups[6].Value), int.Parse(match.Groups[7].Value),
                double.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups[8].Value, CultureInfo.InvariantCulture));
            if (mode.MinWidth <= 0 || mode.MinHeight <= 0 || mode.MaxWidth < mode.MinWidth || mode.MaxHeight < mode.MinHeight
                || mode.MinFps <= 0 || mode.MaxFps < mode.MinFps) continue;
            if (!modes.Contains(mode)) modes.Add(mode);
        }
        return modes;
    }

    [GeneratedRegex("\"(.*?)\"\\s+\\(video\\)")]
    private static partial Regex DevicePattern();
    [GeneratedRegex("Alternative name\\s+\"(.*?)\"")]
    private static partial Regex AlternativePattern();
    [GeneratedRegex(@"(pixel_format|vcodec)=([a-zA-Z0-9_]+)\s+min s=(\d+)x(\d+) fps=([0-9.]+)\s+max s=(\d+)x(\d+) fps=([0-9.]+)")]
    private static partial Regex ModePattern();
}
