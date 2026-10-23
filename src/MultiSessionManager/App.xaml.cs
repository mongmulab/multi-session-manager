using System.Windows;
using System.Windows.Threading;

namespace MultiSessionManager;

public partial class App : Application
{
    public App()
    {
        // 프로세스가 뜬 시각부터 여기까지가 .NET·WPF 기동 몫이다. 느린 구간을 가르는 첫 기준점.
        AppLog.Info($"App 생성 · 프로세스 시작 후 {AppLog.SinceProcessStart.TotalMilliseconds:N0}ms");

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("처리되지 않은 예외(종료)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("관찰되지 않은 작업 예외", e.Exception);
            e.SetObserved();
        };
        WaitForPreviousInstance(Environment.GetCommandLineArgs());
    }

    // "저장 위치 바꾸기 → 그 목록 열기" 로 다시 띄워졌을 때: 앞 프로세스가 세션을 다 정리하고 끝날 때까지 기다린다.
    // 같은 대화를 두 프로세스가 동시에 이어 붙이지 않게 하려는 것이다. 오래 걸리면 그냥 뜬다.
    private static void WaitForPreviousInstance(string[] args)
    {
        var at = Array.IndexOf(args, "--after");
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var pid)) return;
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
            AppLog.Info($"앞 프로세스 {pid} 끝남 확인");
        }
        catch (ArgumentException)
        {
            // 이미 끝났다
        }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI 스레드 예외 하나로 세션 전체(자식 claude 포함)가 같이 죽지 않게 기록하고 넘긴다.
        AppLog.Error("UI 스레드 예외", e.Exception);
        MessageBox.Show(e.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
