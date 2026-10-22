using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiSessionManager.Agents;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

/// <summary>
/// 화면을 PNG 로 그려 본다(눈으로 보는 확인용, 판정 없음). claude 를 띄우지 않고 상태 다섯 가지를 한 장에 꾸며 그린다.
/// 원격 데스크톱이 끊긴 동안에는 Windows 가 WPF 그리기를 멈춰 빈 그림(픽셀 전부 0)이 나온다. 연결한 상태에서 돌린다.
/// MSM_UI_PREVIEW=1 일 때만 돈다. 결과: %TEMP%\msm-ui-preview\
/// </summary>
public sealed class UiPreviewTests
{
    private static readonly string Out = Path.Combine(Path.GetTempPath(), "msm-ui-preview");

    [UiPreviewFact]
    public void Main_window_with_sessions_in_every_state() => OnSta(() =>
    {
        var state = WriteState(
            ("API 서버", @"D:\lab\msm-demo\api"), ("웹 화면", @"D:\lab\msm-demo\web"), ("문서", @"D:\lab\msm-demo\docs"),
            ("배치", @"D:\lab\msm-demo\batch"), ("결제 모듈", @"D:\work\payments"), ("옛 실험", @"D:\work\old"));
        Environment.SetEnvironmentVariable("MSM_STATE_FILE", state);
        var window = new MainWindow();
        var sessions = window.SessionList.Items.Cast<Session>().ToList();
        Fake(sessions[0], SessionStatus.Running, AgentActivity.Waiting, attention: true);
        Fake(sessions[1], SessionStatus.Running, AgentActivity.Working);
        Fake(sessions[2], SessionStatus.Running, AgentActivity.NeedsInput, attention: true);
        Fake(sessions[3], SessionStatus.Running, AgentActivity.Unknown);
        Fake(sessions[4], SessionStatus.Failed, AgentActivity.Unknown, detail: "(2)");
        window.SessionList.SelectedIndex = 0;
        Pump();
        Save((FrameworkElement)window.Content, 1500, 860, "main.png");

        var menu = (ContextMenu)window.Resources["SessionMenu"];
        Save(menu, 230, 230, "menu.png");
    });

    [UiPreviewFact]
    public void Main_window_without_sessions() => OnSta(() =>
    {
        Environment.SetEnvironmentVariable("MSM_STATE_FILE", WriteState());
        var window = new MainWindow();
        Pump();
        Save((FrameworkElement)window.Content, 1100, 640, "empty.png");
    });

    [UiPreviewFact]
    public void Dialogs() => OnSta(() =>
    {
        Save((FrameworkElement)new RenameDialog("API 서버").Content, 400, 190, "rename.png");
        Save((FrameworkElement)new ConfirmDialog("세션 닫기", "'API 서버' 을(를) 닫을까요?",
            "실행 중인 Claude 를 끝내고 목록에서 지웁니다. 대화 기록은 Claude 쪽에 남아 있어, 같은 폴더에서 claude --resume 으로 다시 열 수 있습니다.", "닫기").Content, 420, 190, "confirm.png");
    });

    private static string WriteState(params (string Title, string Dir)[] sessions)
    {
        Directory.CreateDirectory(Out);
        var path = Path.Combine(Out, "state.json");
        var list = sessions.Select((s, i) => new { Id = "s" + i, s.Title, WorkingDirectory = s.Dir, Agent = "claude", AgentSessionId = Guid.NewGuid().ToString() }).ToList();
        var layout = new { Mode = 4, Slots = list.Take(4).Select(s => s.Id).ToArray(), Focus = 0, ColumnRatio = 0.5, RowRatio = 0.5 };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Sessions = list, ActiveSessionId = "s0", Layout = layout }));
        return path;
    }

    // 실제 프로세스 없이 상태만 꾸민다. 그리기 확인용이라 반영(reflection)으로 필드를 바꾼다.
    private static void Fake(Session session, SessionStatus status, AgentActivity activity, bool attention = false, string detail = "")
    {
        var t = typeof(Session);
        t.GetField("_status", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, status);
        t.GetField("_activity", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, activity);
        t.GetField("_statusDetail", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, detail);
        var raise = t.GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var name in new[] { "Status", "Activity", "StatusText" })
            raise.Invoke(session, [name]);
        session.NeedsAttention = attention;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Save(FrameworkElement element, int width, int height, string name)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        Pump();
        const double scale = 1.25; // 이 PC 의 화면 배율로 그린다
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Out);
        using var file = File.Create(Path.Combine(Out, name));
        encoder.Save(file);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // 그래픽 장치 상태와 상관없이 같은 그림이 나오게 소프트웨어로만 그린다.
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                // 앱 자원(Theme.xaml)을 그대로 쓴다. 한 프로세스에 Application 은 하나뿐이다.
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (app.Resources.MergedDictionaries.Count == 0)
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MultiSessionManager;component/Theme.xaml") });
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Exception("그리기 실패", error);
    }
}

public sealed class UiPreviewFactAttribute : FactAttribute
{
    public UiPreviewFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MSM_UI_PREVIEW") != "1") Skip = "MSM_UI_PREVIEW=1 일 때만 그립니다";
    }
}
