using System.Text;

namespace MultiSessionManager.Pty;

public static class EnvironmentBlock
{
    /// <summary>CREATE_UNICODE_ENVIRONMENT 형식: "키=값\0" 을 이어 붙이고 끝에 "\0" 을 하나 더.</summary>
    public static string Build(IReadOnlyDictionary<string, string> variables)
    {
        var sb = new StringBuilder();
        // Windows 는 환경 블록이 대소문자 구분 없이 정렬돼 있기를 기대한다.
        foreach (var (key, value) in variables.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            // "=C:" 처럼 맨 앞의 '=' 는 Windows 숨은 변수라 허용하고, 그 뒤의 '=' 는 블록을 깨뜨린다.
            if (key.Length == 0 || key.IndexOf('=', 1) >= 0)
                continue;
            sb.Append(key).Append('=').Append(value).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    public static Dictionary<string, string> FromCurrentProcess()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            result[(string)e.Key] = (string?)e.Value ?? "";
        return result;
    }
}
