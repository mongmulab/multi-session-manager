using System.Text;

namespace MultiSessionManager.Pty;

/// <summary>
/// CreateProcess 에 넘길 명령줄을 만든다. 규칙은 MSVC 런타임(CommandLineToArgvW)이
/// 다시 쪼갤 때와 같아야 하므로, 역슬래시는 따옴표 바로 앞에서만 두 배로 늘린다.
/// </summary>
public static class CommandLine
{
    public static string Build(string application, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        Append(sb, application);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            Append(sb, arg);
        }
        return sb.ToString();
    }

    public static string Quote(string arg)
    {
        var sb = new StringBuilder();
        Append(sb, arg);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }
            backslashes = 0;
        }
        // 닫는 따옴표 앞의 역슬래시는 두 배로 늘려야 따옴표를 삼키지 않는다.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}
