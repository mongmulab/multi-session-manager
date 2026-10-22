using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MultiSessionManager;

/// <summary>WPF 가 직접 못 하는 창 꾸밈: 어두운 제목 표시줄, 작업 표시줄 깜빡임.</summary>
internal static class NativeWindow
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>
    /// 제목 표시줄을 본문과 같은 어두운 색으로. 직접 그린 제목 표시줄(WindowChrome)은 쓰지 않는다.
    /// 그러면 Windows 11 의 최대화 버튼 위 스냅 레이아웃·창 그림자를 다시 만들어야 해서다.
    /// 지원하지 않는 Windows 에서는 아무 일도 하지 않는다.
    /// </summary>
    public static void UseDarkTitleBar(Window window, uint bgr = 0x00181818)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var color = (int)bgr;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref color, sizeof(int));
    }

    /// <summary>창이 앞에 없을 때 작업 표시줄 단추를 깜빡여 알린다. 창으로 돌아오면 Windows 가 멈춘다.</summary>
    public static void FlashUntilFocused(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 3,
        };
        FlashWindowEx(ref info);
    }

    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_TIMERNOFG = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);
}
