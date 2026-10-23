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

    private static readonly string AppFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiSessionManager");

    /// <summary>처음 깔았을 때의 목록 파일.</summary>
    public static string DefaultFile { get; } = Path.Combine(AppFolder, "sessions.json");

    /// <summary>
    /// 사용자가 고른 저장 위치를 적어 두는 파일. 목록이 어디로 가든 이 파일은 늘 기본 폴더에 둔다.
    /// 앱이 시작할 때 목록을 찾으려면 이것 하나는 정해진 자리에 있어야 한다.
    /// </summary>
    public static string PointerFile { get; } = Path.Combine(AppFolder, "location.json");

    /// <summary>
    /// 목록 파일을 찾는다. MSM_STATE_FILE(시연·녹화·시험용) → 사용자가 고른 위치 → 기본 위치 순이다.
    /// 고른 폴더가 지금 없으면(빠진 USB, 아직 안 붙은 네트워크 드라이브) 기본 위치로 열되 고른 기록은 지우지 않는다.
    /// </summary>
    public static (string Path, StoreSource Source) Locate(string? environmentValue, string pointerFile, string defaultFile)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue))
            return (Path.GetFullPath(environmentValue), StoreSource.Environment);
        var chosen = ReadPointer(pointerFile);
        if (chosen is null)
            return (defaultFile, StoreSource.Default);
        return Directory.Exists(Path.GetDirectoryName(chosen))
            ? (chosen, StoreSource.Chosen)
            : (defaultFile, StoreSource.ChosenMissing);
    }

    public static (string Path, StoreSource Source) Locate() =>
        Locate(Environment.GetEnvironmentVariable("MSM_STATE_FILE"), PointerFile, DefaultFile);

    public static string DefaultPath() => Locate().Path;

    /// <summary>고른 위치를 적는다. 기본 위치(또는 null)면 기록을 지운다.</summary>
    public static void Remember(string pointerFile, string defaultFile, string? file)
    {
        if (file is null || SamePath(file, defaultFile))
        {
            File.Delete(pointerFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(pointerFile)!);
        var temp = pointerFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Pointer { StateFile = Path.GetFullPath(file) }, Options));
        File.Move(temp, pointerFile, overwrite: true);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>고른 위치(기록이 없거나 깨졌으면 null). 그 폴더가 지금 있는지는 보지 않는다.</summary>
    public static string? ReadPointer(string pointerFile)
    {
        try
        {
            if (!File.Exists(pointerFile)) return null;
            var file = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(pointerFile), Options)?.StateFile;
            return string.IsNullOrWhiteSpace(file) ? null : Path.GetFullPath(file);
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or NotSupportedException)
        {
            // 기록이 깨졌으면 기본 위치로 연다. 목록 자체를 잃는 것보다 낫다.
            return null;
        }
    }

    private sealed class Pointer
    {
        public string? StateFile { get; set; }
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

public enum StoreSource
{
    /// <summary>기본 위치(%APPDATA%).</summary>
    Default,
    /// <summary>사용자가 고른 위치.</summary>
    Chosen,
    /// <summary>MSM_STATE_FILE 로 정해짐. 화면에서 바꾸지 않는다.</summary>
    Environment,
    /// <summary>고른 위치가 지금 없어 기본 위치로 열었다.</summary>
    ChosenMissing,
}
