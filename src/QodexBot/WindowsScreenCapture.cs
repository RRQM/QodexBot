using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// 从当前 Windows 交互桌面捕获虚拟屏幕并编码为 PNG。
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal static class WindowsScreenCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint DesktopReadObjects = 0x0001;
    private const uint PwRenderFullContent = 0x00000002;

    /// <summary>
    /// 截取当前用户桌面，包含已连接的多个显示器。
    /// </summary>
    public static byte[] CapturePng()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("桌面截图需要 Windows 7 或更高版本。");

        EnsureInputDesktopIsAccessible();

        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("当前 Windows 会话没有可捕获的桌面显示器。请确认程序运行在已登录的交互桌面中。" );

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// 截取指定进程中面积最大的可见顶层窗口。
    /// </summary>
    public static (byte[] Bytes, string WindowTitle) CaptureProcessWindowPng(string processName)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("进程窗口截图需要 Windows 7 或更高版本。");
        if (string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException("进程名不能为空。", nameof(processName));

        EnsureInputDesktopIsAccessible();

        var normalizedName = Path.GetFileNameWithoutExtension(processName.Trim());
        var processes = Process.GetProcessesByName(normalizedName);
        var processIds = new HashSet<uint>();
        try
        {
            foreach (var process in processes)
            {
                try { processIds.Add((uint)process.Id); }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        if (processIds.Count == 0)
            throw new InvalidOperationException($"没有找到进程“{normalizedName}”。请提供不带 .exe 的进程名，例如 Code 或 notepad。");

        WindowCandidate? selected = null;
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle) || IsIconic(windowHandle)) return true;
            GetWindowThreadProcessId(windowHandle, out var processId);
            if (!processIds.Contains(processId) || !GetWindowRect(windowHandle, out var rect)) return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return true;
            var area = (long)width * height;
            if (selected is null || area > selected.Area)
            {
                var title = new System.Text.StringBuilder(GetWindowTextLength(windowHandle) + 1);
                _ = GetWindowText(windowHandle, title, title.Capacity);
                selected = new WindowCandidate(windowHandle, rect, title.ToString(), area);
            }
            return true;
        }, IntPtr.Zero);

        if (selected is null)
            throw new InvalidOperationException($"进程“{normalizedName}”没有可见且未最小化的窗口。");

        return (CaptureWindowPng(selected), selected.Title);
    }

    private static byte[] CaptureWindowPng(WindowCandidate window)
    {
        var width = window.Rect.Right - window.Rect.Left;
        var height = window.Rect.Bottom - window.Rect.Top;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var deviceContext = graphics.GetHdc();
            bool captured;
            try
            {
                captured = PrintWindow(window.Handle, deviceContext, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }

            if (!captured)
            {
                graphics.CopyFromScreen(window.Rect.Left, window.Rect.Top, 0, 0,
                    new Size(width, height), CopyPixelOperation.SourceCopy);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed record WindowCandidate(IntPtr Handle, NativeRect Rect, string Title, long Area);

    private static void EnsureInputDesktopIsAccessible()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
            throw new InvalidOperationException("无法访问当前 Windows 输入桌面。系统锁屏或会话已断开时，Windows 会阻止普通程序截取桌面；请在已登录且未锁屏的交互桌面中重试。");

        _ = CloseDesktop(desktop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr windowHandle, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
