using System.Diagnostics;

namespace MultiSessionManager;

/// <summary>
/// 하루 한 파일짜리 실행 기록. %LOCALAPPDATA%\MultiSessionManager\logs\app-YYYYMMDD.log
/// 앱을 띄운 뒤 몇 ms 에 무슨 일이 있었는지를 남겨, 느린 구간과 오류를 사후에 볼 수 있게 한다.
/// 입력 내용·화면 출력은 남기지 않는다(대화 내용이 파일로 새지 않게).
/// </summary>
public static class AppLog
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static string? _path;
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    /// <summary>OS 가 프로세스를 만든 뒤 지난 시간. .NET 이 뜨기 전 구간까지 들어간다.</summary>
    public static TimeSpan SinceProcessStart => DateTime.Now - ProcessStart;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiSessionManager", "logs");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                _path ??= InitPath();
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} +{Clock.ElapsedMilliseconds,6}ms {level,-5} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // 기록이 실패해도 앱은 계속 돈다.
        }
        catch (UnauthorizedAccessException) { }
    }

    private static string InitPath()
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }
}
