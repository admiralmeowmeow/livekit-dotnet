namespace LiveKitScreenViewer.Frames;

public sealed class RgbaTestPatternGenerator
{
    private readonly VideoFramePool _framePool;

    public RgbaTestPatternGenerator(VideoFramePool framePool)
    {
        _framePool = framePool;
    }

    public VideoFrame CreateFrame(long frameIndex, int width, int height)
    {
        var stride = width * 4;
        var frame = _framePool.Rent(stride * height, width, height, stride, frameIndex, VideoFrameSource.Synthetic);
        var pixels = frame.PixelSpan;
        var t = (int)(frameIndex % 360);

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var offset = rowOffset + (x * 4);
                byte r = (byte)((x + t * 3) & 0xFF);
                byte g = (byte)((y + t * 2) & 0xFF);
                byte b = (byte)(((x / 8) ^ (y / 8) ^ t) & 0xFF);

                if (x > width / 3 && x < (width / 3) * 2)
                {
                    r = 255;
                }

                if (y > height / 3 && y < (height / 3) * 2)
                {
                    g = 255;
                }

                pixels[offset + 0] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        }

        return frame;
    }
}
