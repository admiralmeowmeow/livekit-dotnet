using System;
using System.Threading;

namespace LiveKitD3D11Viewer.Frames;

public enum VideoFrameSource
{
    Synthetic,
    LiveKit,
}

public sealed class VideoFrame : IDisposable
{
    private readonly VideoFramePool _pool;
    private byte[]? _data;
    private int _byteLength;
    private int _referenceCount;

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

    public VideoFrameSource Source { get; private set; }

    public void Dispose()
    {
        Release();
    }

    internal void Initialize(byte[] data, int byteLength, int width, int height, int stride, long frameIndex, VideoFrameSource source)
    {
        _data = data;
        _byteLength = byteLength;
        Width = width;
        Height = height;
        Stride = stride;
        FrameIndex = frameIndex;
        Source = source;
        Volatile.Write(ref _referenceCount, 1);
    }

    internal bool TryAddReference()
    {
        while (true)
        {
            var currentReferenceCount = Volatile.Read(ref _referenceCount);
            if (currentReferenceCount == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _referenceCount, currentReferenceCount + 1, currentReferenceCount) == currentReferenceCount)
            {
                return true;
            }
        }
    }

    internal void Release()
    {
        var remainingReferences = Interlocked.Decrement(ref _referenceCount);
        if (remainingReferences > 0)
        {
            return;
        }

        if (remainingReferences < 0)
        {
            throw new InvalidOperationException("VideoFrame reference count dropped below zero.");
        }

        _pool.Return(this);
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
        Source = VideoFrameSource.Synthetic;
        return data;
    }
}
