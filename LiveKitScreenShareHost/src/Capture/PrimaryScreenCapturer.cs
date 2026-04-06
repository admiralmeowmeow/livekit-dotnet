using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiveKitScreenShareHost.Capture;

internal sealed class PrimaryScreenCapturer : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly Rectangle _bounds;

    public PrimaryScreenCapturer(DisplayOption display)
    {
        _bounds = display.Bounds;
        _bitmap = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    public Size Resolution => _bounds.Size;

    public CapturedFrame CaptureFrame()
    {
        _graphics.CopyFromScreen(_bounds.Location, Point.Empty, _bounds.Size, CopyPixelOperation.SourceCopy);
        var bitmapData = _bitmap.LockBits(new Rectangle(Point.Empty, _bounds.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        return new CapturedFrame(_bitmap, bitmapData);
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}

internal sealed class CapturedFrame : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly BitmapData _bitmapData;
    private bool _disposed;

    public CapturedFrame(Bitmap bitmap, BitmapData bitmapData)
    {
        _bitmap = bitmap;
        _bitmapData = bitmapData;
    }

    public int Width => _bitmapData.Width;
    public int Height => _bitmapData.Height;
    public int Stride => _bitmapData.Stride;
    public IntPtr DataPointer => _bitmapData.Scan0;

    public unsafe PinnedRgbaFrame CopyAsRgba()
    {
        var rgbaStride = Width * 4;
        var rgbaBuffer = new byte[rgbaStride * Height];
        var sourceStride = Math.Abs(_bitmapData.Stride);

        fixed (byte* destinationBase = rgbaBuffer)
        {
            var sourceBase = (byte*)_bitmapData.Scan0;
            for (var y = 0; y < Height; y++)
            {
                var sourceRow = _bitmapData.Stride >= 0
                    ? sourceBase + (y * sourceStride)
                    : sourceBase + ((Height - 1 - y) * sourceStride);
                var destinationRow = destinationBase + (y * rgbaStride);

                for (var x = 0; x < Width; x++)
                {
                    var sourceOffset = x * 4;
                    var destinationOffset = x * 4;

                    destinationRow[destinationOffset] = sourceRow[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = sourceRow[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceRow[sourceOffset];
                    destinationRow[destinationOffset + 3] = sourceRow[sourceOffset + 3];
                }
            }
        }

        return new PinnedRgbaFrame(rgbaBuffer, Width, Height, rgbaStride);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bitmap.UnlockBits(_bitmapData);
    }
}

internal sealed class PinnedRgbaFrame : IDisposable
{
    private readonly GCHandle _pinnedHandle;
    private bool _disposed;

    public PinnedRgbaFrame(byte[] buffer, int width, int height, int stride)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        _pinnedHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    }

    public byte[] Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public IntPtr DataPointer => _pinnedHandle.AddrOfPinnedObject();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pinnedHandle.IsAllocated)
        {
            _pinnedHandle.Free();
        }
    }
}
