using System;
using System.Threading;

namespace LiveKitScreenViewer.Frames;

public enum VideoFrameSource
{
    Synthetic,
    LiveKit,
}

public sealed class VideoFrame : IDisposable
{
    private readonly VideoFramePool _pool;
    private readonly VideoFrameTimings _timings = new();
    private VideoFrameBuffer? _buffer;
    private int _byteLength;
    private int _referenceCount;

    internal VideoFrame(VideoFramePool pool)
    {
        _pool = pool;
    }

    public IntPtr DataPointer => _buffer?.Pointer ?? IntPtr.Zero;

    public unsafe Span<byte> PixelSpan => _buffer is null
        ? Span<byte>.Empty
        : new Span<byte>((void*)_buffer.Pointer, _byteLength);

    public int ByteLength => _byteLength;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride { get; private set; }

    public long FrameIndex { get; private set; }

    public VideoFrameSource Source { get; private set; }

    public VideoFrameTimings Timings => _timings;

    public void Dispose()
    {
        Release();
    }

    internal void Initialize(VideoFrameBuffer buffer, int byteLength, int width, int height, int stride, long frameIndex, VideoFrameSource source)
    {
        _buffer = buffer;
        _byteLength = byteLength;
        Width = width;
        Height = height;
        Stride = stride;
        FrameIndex = frameIndex;
        Source = source;
        _timings.Reset();
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

    internal VideoFrameBuffer? DetachBuffer()
    {
        var buffer = _buffer;
        _buffer = null;
        _byteLength = 0;
        Width = 0;
        Height = 0;
        Stride = 0;
        FrameIndex = 0;
        Source = VideoFrameSource.Synthetic;
        return buffer;
    }
}
