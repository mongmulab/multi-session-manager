using System.Text.Encodings.Web;
using System.Text.Json;

namespace MultiSessionManager.Sessions;

/// <summary>
/// 탭 구성을 JSON 한 파일에 저장한다. 임시 파일에 다 쓴 뒤 바꿔치기하므로
/// 저장 도중 앱이 죽어도 이전 판이 남는다.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 한글 탭 이름·경로를 \uXXXX 로 바꾸지 않아야 사람이 파일을 열어 볼 수 있다.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }

    public SessionStore(string filePath) => FilePath = filePath;

    /// <summary>MSM_STATE_FILE 로 바꿀 수 있다. 시연·녹화용 목록을 평소 목록과 떼어 둘 때 쓴다.</summary>
    public static string DefaultPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("MSM_STATE_FILE");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiSessionManager", "sessions.json");
    }

    public AppState Load()
    {
        if (!File.Exists(FilePath))
            return new AppState();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();
        }
        catch (JsonException)
        {
            // 깨진 파일은 지우지 않고 옆에 남겨 둔다. 사용자가 손으로 살릴 수 있다.
            var backup = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(FilePath, backup, overwrite: true);
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        if (File.Exists(FilePath))
            File.Replace(temp, FilePath, null);
        else
            File.Move(temp, FilePath);
    }
}
