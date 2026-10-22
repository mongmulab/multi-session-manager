using MultiSessionManager.Agents;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

public sealed class WindowPlacementTests : IDisposable
{
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msm-place-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "sessions.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static WindowPlacement.WINDOWPLACEMENT Native(int showCmd, int flags = 0) => new()
    {
        showCmd = showCmd,
        flags = flags,
        rcNormalPosition = new WindowPlacement.RECT { Left = 100, Top = 50, Right = 1300, Bottom = 850 },
    };

    [Fact]
    public void Normal_window_keeps_its_rectangle()
    {
        var b = WindowPlacement.FromNative(Native(SW_SHOWNORMAL));
        Assert.Equal((100, 50, 1200, 800, false), (b.Left, b.Top, b.Width, b.Height, b.Maximized));
    }

    [Fact]
    public void Maximized_window_remembers_restore_size_and_flag()
    {
        var b = WindowPlacement.FromNative(Native(SW_SHOWMAXIMIZED));
        Assert.True(b.Maximized);
        Assert.Equal(1200, b.Width);
    }

    [Fact]
    public void Minimized_window_reopens_in_the_state_it_was_minimized_from()
    {
        Assert.True(WindowPlacement.FromNative(Native(SW_SHOWMINIMIZED, flags: 0x2)).Maximized);
        Assert.False(WindowPlacement.FromNative(Native(SW_SHOWMINIMIZED)).Maximized);
    }

    [Fact]
    public void Never_restores_minimized()
    {
        var wp = WindowPlacement.ToNative(new WindowBounds { Left = 0, Top = 0, Width = 800, Height = 600 })!.Value;
        Assert.Equal(SW_SHOWNORMAL, wp.showCmd);
        var max = WindowPlacement.ToNative(new WindowBounds { Width = 800, Height = 600, Maximized = true })!.Value;
        Assert.Equal(SW_SHOWMAXIMIZED, max.showCmd);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(399, 600)]
    [InlineData(800, -5)]
    public void Too_small_or_broken_size_falls_back_to_default(int width, int height)
    {
        Assert.Null(WindowPlacement.ToNative(new WindowBounds { Width = width, Height = height }));
    }

    [Fact]
    public void Round_trip_through_native_is_exact()
    {
        var saved = new WindowBounds { Left = -1800, Top = 120, Width = 1500, Height = 900, Maximized = true };
        var back = WindowPlacement.FromNative(WindowPlacement.ToNative(saved)!.Value);
        Assert.Equal((saved.Left, saved.Top, saved.Width, saved.Height, saved.Maximized), (back.Left, back.Top, back.Width, back.Height, back.Maximized));
    }

    [Fact]
    public void Window_survives_unrelated_saves()
    {
        using (var manager = new SessionManager(new SessionStore(FilePath), new NullAdapter()))
        {
            manager.Load();
            var s = manager.Create(@"D:\work\api");
            manager.Window = new WindowBounds { Left = 10, Top = 20, Width = 1000, Height = 700 };
            manager.Save();
            manager.Rename(s, "다른 이름"); // 창 위치와 상관없는 저장이 한 번 더 지나간다
        }

        using var reloaded = new SessionManager(new SessionStore(FilePath), new NullAdapter());
        reloaded.Load();
        Assert.Equal(1000, reloaded.Window!.Width);
        Assert.Equal("다른 이름", Assert.Single(reloaded.Sessions).Title);
    }

    [Fact]
    public void Old_file_without_window_loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"Version":1,"Sessions":[{"Id":"a","Title":"api","WorkingDirectory":"D:/work"}]}""");
        using var manager = new SessionManager(new SessionStore(FilePath), new NullAdapter());
        manager.Load();
        Assert.Null(manager.Window);
        Assert.Equal("api", Assert.Single(manager.Sessions).Title);
    }

    private sealed class NullAdapter : IAgentAdapter
    {
        public string Kind => "null";
        public AgentLaunch Prepare(SessionRecord session, int columns, int rows) => throw new NotSupportedException();
        public bool HasConversation(SessionRecord session) => false;
    }
}
