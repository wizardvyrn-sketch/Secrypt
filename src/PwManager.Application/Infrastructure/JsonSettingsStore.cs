using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Ports;

namespace PwManager.Application.Infrastructure;

/// <summary>
/// 設定を JSON ファイルに保存する。壊れていれば既定値で読み込む。
/// 最後に使った鍵ファイルの場所は、<paramref name="protector"/> があれば暗号化して <c>LastKeyfilePathProtected</c> に保存する
/// （保管ファイルと設定ファイルが一緒に流出したとき、鍵ファイルの置き場所まで分からないように）。
/// 以前の版が平文で保存した <c>LastKeyfilePath</c> は、読み込んだときに暗号化して書き直す。
/// </summary>
public sealed class JsonSettingsStore(string path, ILogger<JsonSettingsStore>? logger = null, ISettingsProtector? protector = null) : ISettingsStore
{
    private const string PlainKeyfilePath = nameof(AppSettings.LastKeyfilePath);
    private const string ProtectedKeyfilePath = "LastKeyfilePathProtected";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILogger _log = logger ?? NullLogger<JsonSettingsStore>.Instance;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            var protectedPath = node[ProtectedKeyfilePath]?.GetValue<string>();
            node.Remove(ProtectedKeyfilePath);
            var settings = (node.Deserialize<AppSettings>() ?? new AppSettings()).Normalize();

            if (protectedPath is not null)
            {
                settings.LastKeyfilePath = protector?.Unprotect(protectedPath);
            }
            else if (protector is not null && settings.LastKeyfilePath is not null)
            {
                Save(settings); // 以前の版の平文を暗号化して書き直す
            }
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Log.SettingsLoadFailed(_log, ex.GetType().Name);
        }
        return new AppSettings();
    }

    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
            if (protector is not null)
            {
                node.Remove(PlainKeyfilePath);
                if (settings.LastKeyfilePath is { } keyfilePath) node[ProtectedKeyfilePath] = protector.Protect(keyfilePath);
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, node.ToJsonString(JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // 暗号化できないときも平文では保存しない
            Log.SettingsSaveFailed(_log, ex.GetType().Name);
            return false;
        }
    }
}
