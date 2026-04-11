using System;
using System.Runtime.InteropServices;
using LiveKitScreenViewer.Controls;
using LiveKitScreenViewer.Frames;

namespace LiveKitScreenViewer.LiveKit;

public sealed class LiveKitFrameBridge
{
    private readonly VideoView _videoView;

    public LiveKitFrameBridge(VideoView videoView)
    {
        _videoView = videoView;
    }

    public void SubmitRgbaFrame(IntPtr sourceData, int byteLength, int width, int height, int stride, long frameIndex)
    {
        var frame = _videoView.FramePool.Rent(byteLength, width, height, stride, frameIndex, VideoFrameSource.LiveKit);
        unsafe
        {
            fixed (byte* destination = frame.Data)
            {
                Buffer.MemoryCopy((void*)sourceData, destination, frame.Data.Length, byteLength);
            }
        }

        _videoView.SubmitFrame(frame);
    }
}
