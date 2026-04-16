using System.Buffers;
using System.Collections.Concurrent;

namespace LiveKitD3D11Viewer.Frames;

public sealed class VideoFramePool
{
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly ConcurrentBag<VideoFrame> _framePool = [];

    public VideoFrame Rent(int byteLength, int width, int height, int stride, long frameIndex)
    {
        var frame = _framePool.TryTake(out var pooledFrame)
            ? pooledFrame
            : new VideoFrame(this);

        var data = _bufferPool.Rent(byteLength);
        frame.Initialize(data, byteLength, width, height, stride, frameIndex);
        return frame;
    }

    internal void Return(VideoFrame frame)
    {
        var data = frame.DetachData();
        if (data is not null)
        {
            _bufferPool.Return(data);
        }

        _framePool.Add(frame);
    }
}
