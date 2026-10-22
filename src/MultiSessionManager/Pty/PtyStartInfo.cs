namespace MultiSessionManager.Pty;

public sealed record PtyStartInfo(
    string ApplicationPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Columns = 120,
    int Rows = 30,
    // 끝낼 때 먼저 보내 볼 입력. 사용자가 나가는 것과 같은 길로 나가게 한다(예: Claude 는 Ctrl+C 두 번).
    IReadOnlyList<string>? QuitInput = null);
