using System.Runtime.InteropServices;

namespace MultiSessionManager;

/// <summary>디스크에 남기는 창 위치. 좌표는 화면 픽셀이고, 최대화 상태여도 되돌렸을 때의 크기를 적는다.</summary>
public sealed class WindowBounds
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// 창 위치를 GetWindowPlacement / SetWindowPlacement 로 저장·복원한다.
/// 좌표를 직접 계산하지 않는 이유는, 저장했던 모니터가 빠졌을 때 화면 밖으로 나갈 창을
/// SetWindowPlacement 가 알아서 보이는 곳으로 옮겨 주기 때문이다. 배율이 다른 모니터도 Windows 가 맞춘다.
/// </summary>
internal static class WindowPlacement
{
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 0x2;

    // 이보다 작은 값은 저장 파일이 깨졌거나 손으로 잘못 고친 것으로 본다.
    internal const int MinWidth = 400;
    internal const int MinHeight = 300;

    public static WindowBounds? Capture(IntPtr hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return null;
        return FromNative(wp);
    }

    public static void Apply(IntPtr hwnd, WindowBounds bounds)
    {
        if (ToNative(bounds) is { } wp)
            SetWindowPlacement(hwnd, ref wp);
    }

    internal static WindowBounds FromNative(WINDOWPLACEMENT wp)
    {
        var r = wp.rcNormalPosition;
        // 최소화한 채 닫았으면, 최소화 직전 상태(최대화였는지)로 되살린다.
        var maximized = wp.showCmd == SW_SHOWMAXIMIZED
            || (wp.showCmd == SW_SHOWMINIMIZED && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0);
        return new WindowBounds
        {
            Left = r.Left,
            Top = r.Top,
            Width = r.Right - r.Left,
            Height = r.Bottom - r.Top,
            Maximized = maximized,
        };
    }

    /// <returns>쓸 수 없는 값이면 null. 그때는 XAML 기본 크기로 연다.</returns>
    internal static WINDOWPLACEMENT? ToNative(WindowBounds b)
    {
        if (b.Width < MinWidth || b.Height < MinHeight) return null;
        return new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            // 최소화로는 절대 열지 않는다. 앱을 켰는데 창이 안 보이게 된다.
            showCmd = b.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
            ptMinPosition = new POINT { X = -1, Y = -1 },
            ptMaxPosition = new POINT { X = -1, Y = -1 },
            rcNormalPosition = new RECT { Left = b.Left, Top = b.Top, Right = b.Left + b.Width, Bottom = b.Top + b.Height },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
}
