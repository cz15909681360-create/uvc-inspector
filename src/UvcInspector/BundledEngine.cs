using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UvcInspector.Core;

namespace UvcInspector;

public static class BundledEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string BinaryResource = "UvcInspector.Engine.ffmpeg.exe";
    private const string HashResource = "UvcInspector.Engine.sha256";

    public static bool Available => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(BinaryResource);
    public static bool IsBundledPath(string path) => Path.GetFullPath(path).StartsWith(
        Path.Combine(Settings.DirectoryPath, "engines") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static async Task<string?> ResolveAsync(string? preferred, CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)) return Path.GetFullPath(preferred);
        if (!Available) return FfmpegEngine.FindExecutable();
        return await Task.Run(() => ExtractAsync(cancellation), cancellation);
    }

    internal static Task<string> ExtractForChecksAsync(string cacheRoot, CancellationToken cancellation)
        => Task.Run(() => ExtractAsync(cancellation, cacheRoot), cancellation);

    private static async Task<string> ExtractAsync(CancellationToken cancellation, string? cacheRoot = null)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var hashStream = assembly.GetManifestResourceStream(HashResource) ?? throw new InvalidDataException("内置引擎缺少校验信息。");
        using var reader = new StreamReader(hashStream);
        string hash = (await reader.ReadToEndAsync(cancellation)).Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("内置引擎校验信息无效。");
        string directory = Path.Combine(cacheRoot ?? Path.Combine(Settings.DirectoryPath, "engines"), hash);
        string target = Path.Combine(directory, "ffmpeg.exe");
        await Gate.WaitAsync(cancellation);
        try
        {
            Directory.CreateDirectory(directory);
            await using var processLock = await AcquireLockAsync(Path.Combine(directory, "extract.lock"), cancellation);
            if (await MatchesAsync(target, hash, cancellation)) return target;
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var source = assembly.GetManifestResourceStream(BinaryResource) ?? throw new InvalidDataException("内置引擎资源缺失。"))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await source.CopyToAsync(output, cancellation);
                if (!await MatchesAsync(temporary, hash, cancellation)) throw new InvalidDataException("内置引擎校验失败，请重新下载软件。");
                File.Move(temporary, target, true);
                return target;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { Gate.Release(); }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellation)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(100, cancellation); }
        }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken cancellation)
    {
        if (!File.Exists(path)) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
