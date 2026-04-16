using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiveKitScreenShareHost.Capture;

internal sealed class PrimaryScreenCapturer : IDisposable
{
    private readonly ICaptureBackend _backend;

    public PrimaryScreenCapturer(DisplayOption display)
    {
        if (!WgcScreenCapturer.IsSupported)
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this machine.");
        }

        _backend = new WgcScreenCapturer(display);
    }

    public Size Resolution => _backend.Resolution;

    public string BackendName => _backend.BackendName;

    public bool IsFrameDriven => _backend.IsFrameDriven;

    public CapturedFrame CaptureFrame()
    {
        return _backend.CaptureFrame();
    }

    public void Dispose()
    {
        _backend.Dispose();
    }

    internal RgbaFramePool FramePool => _backend.FramePool;
}

internal interface ICaptureBackend : IDisposable
{
    Size Resolution { get; }

    RgbaFramePool FramePool { get; }

    string BackendName { get; }

    bool IsFrameDriven { get; }

    CapturedFrame CaptureFrame();
}

internal sealed class GdiScreenCapturer : ICaptureBackend
{
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly Rectangle _bounds;
    private readonly RgbaFramePool _rgbaFramePool;

    public GdiScreenCapturer(DisplayOption display)
    {
        _bounds = display.Bounds;
        _bitmap = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _rgbaFramePool = new RgbaFramePool(_bounds.Width * _bounds.Height * 4);
    }

    public Size Resolution => _bounds.Size;

    public RgbaFramePool FramePool => _rgbaFramePool;

    public string BackendName => "GDI";

    public bool IsFrameDriven => false;

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
        _rgbaFramePool.Dispose();
    }
}

internal sealed class CapturedFrame : IDisposable
{
    private readonly Bitmap? _bitmap;
    private readonly BitmapData? _bitmapData;
    private readonly FrameDataLease? _frameDataLease;
    private bool _disposed;

    public CapturedFrame(Bitmap bitmap, BitmapData bitmapData)
    {
        _bitmap = bitmap;
        _bitmapData = bitmapData;
        Width = bitmapData.Width;
        Height = bitmapData.Height;
        Stride = bitmapData.Stride;
        DataPointer = bitmapData.Scan0;
    }

    public CapturedFrame(FrameDataLease frameDataLease)
    {
        _frameDataLease = frameDataLease;
        Width = frameDataLease.Width;
        Height = frameDataLease.Height;
        Stride = frameDataLease.Stride;
        DataPointer = frameDataLease.DataPointer;
    }

    public CapturedFrame(IDisposable reference, int width, int height, int stride, IntPtr dataPointer)
        : this(new FrameDataLease(reference, dataPointer, width, height, stride))
    {
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public IntPtr DataPointer { get; }

    public unsafe PinnedRgbaFrame CopyAsRgba(RgbaFramePool framePool)
    {
        var rgbaStride = Width * 4;
        var rgbaBuffer = framePool.Rent(rgbaStride * Height);
        var sourceStride = Math.Abs(Stride);
        var destinationBase = (uint*)rgbaBuffer.Pointer;
        var sourceBase = (byte*)DataPointer;

        for (var y = 0; y < Height; y++)
        {
            var sourceRow = Stride >= 0
                ? (uint*)(sourceBase + (y * sourceStride))
                : (uint*)(sourceBase + ((Height - 1 - y) * sourceStride));
            var destinationRow = destinationBase + (y * Width);

            var x = 0;
            var pairCount = Width / 2;
            var sourcePairs = (ulong*)sourceRow;
            var destinationPairs = (ulong*)destinationRow;
            for (var pair = 0; pair < pairCount; pair++, x += 2)
            {
                var bgraPair = sourcePairs[pair];
                destinationPairs[pair] =
                    (bgraPair & 0xFF00FF00FF00FF00UL) |
                    ((bgraPair & 0x00FF000000FF0000UL) >> 16) |
                    ((bgraPair & 0x000000FF000000FFUL) << 16);
            }

            if ((Width & 1) != 0)
            {
                var bgra = sourceRow[x];
                destinationRow[x] = (bgra & 0xFF00FF00u) | ((bgra & 0x00FF0000u) >> 16) | ((bgra & 0x000000FFu) << 16);
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
        _frameDataLease?.Dispose();
        if (_bitmap is not null && _bitmapData is not null)
        {
            _bitmap.UnlockBits(_bitmapData);
        }
    }
}

internal sealed class FrameDataLease : IDisposable
{
    private readonly IDisposable _reference;
    private bool _disposed;

    public FrameDataLease(IDisposable reference, IntPtr dataPointer, int width, int height, int stride)
    {
        _reference = reference;
        DataPointer = dataPointer;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public IntPtr DataPointer { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reference.Dispose();
    }
}

internal sealed class PinnedRgbaFrame : IDisposable
{
    private readonly RgbaFrameBuffer _buffer;
    private bool _disposed;

    public PinnedRgbaFrame(RgbaFrameBuffer buffer, int width, int height, int stride)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public IntPtr DataPointer => _buffer.Pointer;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.Return();
    }
}

internal sealed class RgbaFramePool : IDisposable
{
    private readonly ConcurrentBag<RgbaFrameBuffer> _buffers = new();
    private readonly int _minimumCapacity;
    private bool _disposed;

    public RgbaFramePool(int minimumCapacity)
    {
        _minimumCapacity = minimumCapacity;
    }

    public RgbaFrameBuffer Rent(int requiredCapacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_buffers.TryTake(out var buffer))
        {
            if (buffer.Capacity >= requiredCapacity)
            {
                return buffer;
            }

            buffer.Dispose();
        }

        return new RgbaFrameBuffer(this, Math.Max(_minimumCapacity, requiredCapacity));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_buffers.TryTake(out var buffer))
        {
            buffer.Dispose();
        }
    }

    internal void Return(RgbaFrameBuffer buffer)
    {
        if (_disposed)
        {
            buffer.Dispose();
            return;
        }

        _buffers.Add(buffer);
    }
}

internal sealed class RgbaFrameBuffer : IDisposable
{
    private readonly RgbaFramePool _owner;
    private bool _disposed;

    public RgbaFrameBuffer(RgbaFramePool owner, int capacity)
    {
        _owner = owner;
        Capacity = capacity;
        Pointer = Marshal.AllocHGlobal(capacity);
    }

    public int Capacity { get; }

    public IntPtr Pointer { get; }

    public void Return()
    {
        if (_disposed)
        {
            return;
        }

        _owner.Return(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Pointer);
        }
    }
}
