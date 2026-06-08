using ICE.Utilities.Cosmic_Helper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ICE.Utilities;

// Artisanのマクロ一覧を取得するヘルパー。
// ArtisanのIPCにはマクロ一覧取得メソッドが無いため、Artisanの設定ファイル(Artisan.json)の
// MacroSolverConfig.Macros[] から ID / Name を読み取る。ミッション別Artisan設定の「Macro」ソルバーで
// マクロ名をプルダウン選択できるようにするために使う。保存値(MissionConfig.ArtisanSettings.MacroName)は
// 従来どおりマクロ名(string)のままで、ChangeSolverへ "Macro: {名前}" を渡す既存フローは無改修。
public static class ArtisanMacroHelper
{
    public record MacroEntry(uint Id, string Name);

    private static List<MacroEntry> _cache = new();
    private static bool _loaded = false;

    /// <summary>キャッシュされたマクロ一覧(初回アクセス時に遅延ロード)。</summary>
    public static IReadOnlyList<MacroEntry> Macros
    {
        get
        {
            if (!_loaded)
                Reload();
            return _cache;
        }
    }

    /// <summary>Artisan.json を読み直してマクロ一覧を更新する(Artisanで新規作成した直後の反映用)。</summary>
    public static void Reload()
    {
        _loaded = true;
        var list = new List<MacroEntry>();
        try
        {
            // ICEの設定ファイルと同じ pluginConfigs フォルダ内の Artisan.json を参照する。
            var dir = Svc.PluginInterface.ConfigFile.DirectoryName;
            if (string.IsNullOrEmpty(dir))
            {
                _cache = list;
                return;
            }
            var path = Path.Combine(dir, "Artisan.json");
            if (!File.Exists(path))
            {
                _cache = list;
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("MacroSolverConfig", out var msc)
                && msc.TryGetProperty("Macros", out var macros)
                && macros.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in macros.EnumerateArray())
                {
                    uint id = 0;
                    if (m.TryGetProperty("ID", out var idEl) && idEl.TryGetUInt32(out var parsedId))
                        id = parsedId;
                    string name = m.TryGetProperty("Name", out var nEl) ? (nEl.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add(new MacroEntry(id, name));
                }
            }
        }
        catch (Exception ex)
        {
            IceLogging.Warning($"[ArtisanMacroHelper] Artisanマクロ一覧の読み取りに失敗しました(手入力で対応してください): {ex.Message}");
        }
        _cache = list;
    }
}
