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

    public void SubmitRgbaFrame(byte[] rgba, int width, int height, int stride, long frameIndex)
    {
        _videoView.SubmitFrame(new VideoFrame(rgba, width, height, stride, frameIndex, VideoFrameSource.LiveKit));
    }
}
