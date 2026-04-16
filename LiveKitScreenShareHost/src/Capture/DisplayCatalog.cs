using System.Drawing;
using System.Runtime.InteropServices;

namespace LiveKitScreenShareHost.Capture;

internal static class DisplayCatalog
{
    private const int EnumCurrentSettings = -1;
    private const int MonitorInfoFPrimary = 0x00000001;

    public static IReadOnlyList<DisplayOption> GetActiveDisplays()
    {
        var displays = new List<DisplayOption>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___) =>
        {
            var info = new MonitorInfoEx
            {
                cbSize = Marshal.SizeOf<MonitorInfoEx>(),
                szDevice = string.Empty,
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var bounds = Rectangle.FromLTRB(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right,
                info.rcMonitor.Bottom);
            var isPrimary = (info.dwFlags & MonitorInfoFPrimary) != 0;
            var refreshRate = TryGetRefreshRate(info.szDevice);
            var displayName = $"Display {displays.Count + 1}" + (isPrimary ? " (Primary)" : string.Empty);
            var description = $"{info.szDevice} | {bounds.Width} x {bounds.Height} @ {refreshRate} Hz";

            displays.Add(new DisplayOption(
                MonitorHandle: monitor,
                ScreenDeviceName: info.szDevice,
                DisplayName: displayName,
                Description: description,
                Bounds: bounds,
                RefreshRate: refreshRate,
                IsPrimary: isPrimary));

            return true;
        }, IntPtr.Zero);

        return displays;
    }

    private static int TryGetRefreshRate(string deviceName)
    {
        var mode = new DevMode
        {
            dmSize = (short)Marshal.SizeOf<DevMode>(),
        };

        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
        {
            return 60;
        }

        return mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 60;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}

public sealed record DisplayOption(
    IntPtr MonitorHandle,
    string ScreenDeviceName,
    string DisplayName,
    string Description,
    Rectangle Bounds,
    int RefreshRate,
    bool IsPrimary);
