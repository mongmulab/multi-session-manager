namespace MultiSessionManager.Pty;

public sealed record PtyStartInfo(
    string ApplicationPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Columns = 120,
    int Rows = 30);
