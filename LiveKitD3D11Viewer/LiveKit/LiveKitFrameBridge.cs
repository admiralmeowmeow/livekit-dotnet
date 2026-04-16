using System.Runtime.InteropServices;
using LiveKitD3D11Viewer.Controls;

namespace LiveKitD3D11Viewer.LiveKit;

public sealed class LiveKitFrameBridge
{
    private readonly VideoView _videoView;

    public LiveKitFrameBridge(VideoView videoView)
    {
        _videoView = videoView;
    }

    public void SubmitRgbaFrame(IntPtr sourceData, int byteLength, int width, int height, int stride, long frameIndex)
    {
        var frame = _videoView.FramePool.Rent(byteLength, width, height, stride, frameIndex);
        unsafe
        {
            fixed (byte* destination = frame.Data)
            {
                System.Buffer.MemoryCopy((void*)sourceData, destination, frame.ByteLength, byteLength);
            }
        }

        _videoView.SubmitFrame(frame);
    }
}
