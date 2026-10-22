namespace MultiSessionManager.Agents;

public static class ExecutableLocator
{
    /// <summary>
    /// PATH 에서 실행 파일을 찾는다. 네이티브 설치(claude.exe)를 먼저 보고,
    /// 없으면 npm 설치(claude.cmd)를 본다. .cmd 는 cmd.exe 를 거쳐야 하므로 따로 알려 준다.
    /// </summary>
    public static string? Find(string name, string? pathVariable = null, IEnumerable<string>? extensions = null)
    {
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = (extensions ?? [".exe", ".cmd", ".bat"]).ToList();

        foreach (var ext in exts)
        {
            foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim().Trim('"'), name + ext);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public static bool NeedsCmdShell(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
