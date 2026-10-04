namespace UvcInspector.Core;

/// <summary>Split the bounded MJPEG preview stream without buffering the whole video.</summary>
public static class JpegFrameReader
{
    public static async Task ReadAsync(Stream input, Action<byte[]> frame, CancellationToken cancellation)
    {
        var buffer = new byte[32_768];
        using var current = new MemoryStream();
        int previous = -1;
        bool inside = false;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (read == 0) break;
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                if (!inside && previous == 0xff && value == 0xd8)
                {
                    current.SetLength(0);
                    current.WriteByte(0xff);
                    inside = true;
                }
                if (inside)
                {
                    current.WriteByte(value);
                    if (current.Length > 4 * 1024 * 1024) throw new InvalidDataException("预览图像超出大小限制。");
                    if (previous == 0xff && value == 0xd9)
                    {
                        frame(current.ToArray());
                        inside = false;
                    }
                }
                previous = value;
            }
        }
        if (inside) throw new InvalidDataException("预览图像不完整，设备可能已断开。");
    }
}
