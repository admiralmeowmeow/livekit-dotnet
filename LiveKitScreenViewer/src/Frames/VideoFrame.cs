namespace LiveKitScreenViewer.Frames;

public enum VideoFrameSource
{
    Synthetic,
    LiveKit,
}

public sealed class VideoFrame
{
    public VideoFrame(byte[] data, int width, int height, int stride, long frameIndex, VideoFrameSource source = VideoFrameSource.Synthetic)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
        FrameIndex = frameIndex;
        Source = source;
    }

    public byte[] Data { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public long FrameIndex { get; }

    public VideoFrameSource Source { get; }
}
