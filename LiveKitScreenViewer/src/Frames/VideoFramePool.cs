using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LiveKitScreenViewer.Frames;

public sealed class VideoFramePool
{
    private readonly ConcurrentBag<VideoFrameBuffer> _bufferPool = [];
    private readonly ConcurrentBag<VideoFrame> _framePool = [];

    public VideoFrame Rent(int byteLength, int width, int height, int stride, long frameIndex, VideoFrameSource source)
    {
        var frame = _framePool.TryTake(out var pooledFrame)
            ? pooledFrame
            : new VideoFrame(this);

        var buffer = RentBuffer(byteLength);
        frame.Initialize(buffer, byteLength, width, height, stride, frameIndex, source);
        return frame;
    }

    internal void Return(VideoFrame frame)
    {
        var buffer = frame.DetachBuffer();
        if (buffer is not null)
        {
            _bufferPool.Add(buffer);
        }

        _framePool.Add(frame);
    }

    private VideoFrameBuffer RentBuffer(int minimumByteLength)
    {
        while (_bufferPool.TryTake(out var buffer))
        {
            if (buffer.Capacity >= minimumByteLength)
            {
                return buffer;
            }

            buffer.Dispose();
        }

        return new VideoFrameBuffer(minimumByteLength);
    }
}

public sealed class VideoFrameBuffer : IDisposable
{
    public VideoFrameBuffer(int capacity)
    {
        Capacity = capacity;
        Pointer = Marshal.AllocHGlobal(capacity);
    }

    public IntPtr Pointer { get; private set; }

    public int Capacity { get; }

    public void Dispose()
    {
        if (Pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeHGlobal(Pointer);
        Pointer = IntPtr.Zero;
    }
}
