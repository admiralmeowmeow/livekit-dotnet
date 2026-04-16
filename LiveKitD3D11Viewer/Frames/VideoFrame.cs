namespace LiveKitD3D11Viewer.Frames;

public sealed class VideoFrame : IDisposable
{
    private readonly VideoFramePool _pool;
    private byte[]? _data;
    private int _byteLength;

    internal VideoFrame(VideoFramePool pool)
    {
        _pool = pool;
    }

    public byte[] Data => _data ?? Array.Empty<byte>();

    public Span<byte> PixelSpan => Data.AsSpan(0, _byteLength);

    public int ByteLength => _byteLength;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride { get; private set; }

    public long FrameIndex { get; private set; }

    public void Dispose()
    {
        _pool.Return(this);
    }

    internal void Initialize(byte[] data, int byteLength, int width, int height, int stride, long frameIndex)
    {
        _data = data;
        _byteLength = byteLength;
        Width = width;
        Height = height;
        Stride = stride;
        FrameIndex = frameIndex;
    }

    internal byte[]? DetachData()
    {
        var data = _data;
        _data = null;
        _byteLength = 0;
        Width = 0;
        Height = 0;
        Stride = 0;
        FrameIndex = 0;
        return data;
    }
}
