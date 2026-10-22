using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace MultiSessionManager;

public partial class App : Application
{
    public App()
    {
        // 프로세스가 뜬 시각부터 여기까지가 .NET·WPF 기동 몫이다. 느린 구간을 가르는 첫 기준점.
        var sinceProcessStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
        AppLog.Info($"App 생성 · 프로세스 시작 후 {sinceProcessStart.TotalMilliseconds:N0}ms");

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("처리되지 않은 예외(종료)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("관찰되지 않은 작업 예외", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI 스레드 예외 하나로 세션 전체(자식 claude 포함)가 같이 죽지 않게 기록하고 넘긴다.
        AppLog.Error("UI 스레드 예외", e.Exception);
        MessageBox.Show(e.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
