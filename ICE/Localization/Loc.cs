using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ICE.Localization;

/// <summary>UI の表示言語。Auto はゲームクライアントの言語に従う。</summary>
public enum UiLanguage
{
    Auto = 0,
    English = 1,
    Japanese = 2,
}

/// <summary>
/// プラグイン内蔵の日本語化。CSV 辞書(Plugin,Menu,Section,Type,English,Japanese)を読み、
/// 描画時に <see cref="T(string)"/> で英語文字列を日本語へ置き換える。外部の翻訳プラグインには依存しない。
/// 辞書は同梱の localization\ICE_v1.0.csv と、設定フォルダ localization\*.csv(ユーザー上書き、後勝ち)から読む。
/// 「##id」付きのラベルは辞書に全文(##込み)で載せる慣例だが、無い場合は ## より前の部分で引いて id を付け直す。
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private static bool _enabled;
    private static string _status = "";

    public static bool Enabled => _enabled;
    public static int Count => _map.Count;
    public static string Status => _status;
    /// <summary>辞書に無かった英語文字列(翻訳漏れの洗い出し用)。</summary>
    public static IReadOnlyCollection<string> Missing => _missing;

    public static string BundledCsvDirectory =>
        Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName ?? "", "localization");

    public static string BundledCsvPath => Path.Combine(BundledCsvDirectory, "ICE_v1.0.csv");

    public static string UserCsvDirectory =>
        Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "localization");

    /// <summary>設定(UiLanguage)とクライアント言語から有効/無効を決め、辞書を読み込む。設定変更時にも呼ぶ。</summary>
    public static void Initialize()
    {
        var lang = C.UiLanguage;
        bool japanese = lang switch
        {
            UiLanguage.Japanese => true,
            UiLanguage.English => false,
            _ => Svc.ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.Japanese,
        };
        _enabled = japanese;
        if (!_enabled)
        {
            _status = "UI language: English";
            return;
        }
        Reload();
    }

    /// <summary>辞書ファイルを読み直す。</summary>
    public static void Reload()
    {
        _map.Clear();
        _missing.Clear();
        int files = 0;
        var sources = new List<string>();
        // 同梱辞書は名前順(ICE_v0.3 → ICE_v1.0)。後に読んだものが勝つので新しい辞書が優先される
        try
        {
            if (Directory.Exists(BundledCsvDirectory))
                sources.AddRange(Directory.GetFiles(BundledCsvDirectory, "*.csv", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
        try
        {
            if (Directory.Exists(UserCsvDirectory))
                sources.AddRange(Directory.GetFiles(UserCsvDirectory, "*.csv", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        catch { }

        foreach (var path in sources)
        {
            try
            {
                int n = LoadCsv(path);
                files++;
                IceLoggingSafe($"辞書を読み込みました: {Path.GetFileName(path)} ({n} 件)");
            }
            catch (Exception ex)
            {
                IceLoggingSafe($"辞書の読み込みに失敗: {path}: {ex.Message}");
            }
        }
        _status = files == 0
            ? $"辞書が見つかりません: {BundledCsvPath}"
            : $"UI language: Japanese ({_map.Count} entries from {files} file(s))";
    }

    private static void IceLoggingSafe(string msg)
    {
        try { global::ICE.Utilities.Cosmic_Helper.IceLogging.Info(msg, "[Loc]"); } catch { }
    }

    /// <summary>英語の UI 文字列を日本語にする。辞書に無ければそのまま返す。</summary>
    public static string T(string s)
    {
        if (!_enabled || string.IsNullOrEmpty(s))
            return s;
        if (_map.TryGetValue(s, out var t))
            return t;
        // 改行コード差(\r\n)を吸収
        if (s.Contains('\r') && _map.TryGetValue(s.Replace("\r\n", "\n"), out t))
            return t;
        // 「表示文##id」の慣例: 表示文だけで引けたら id を付け直す
        int hash = s.IndexOf("##", StringComparison.Ordinal);
        if (hash > 0 && _map.TryGetValue(s[..hash], out t))
            return t + s[hash..];
        if (hash != 0 && _missing.Count < 2000)
            _missing.Add(s);
        return s;
    }

    /// <summary>複数行や連結で作った文字列も同じ辞書で引く(T の別名、読みやすさ用)。</summary>
    public static string F(string s) => T(s);

    // ---- CSV ----

    private static int LoadCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var records = ParseCsv(text);
        if (records.Count == 0) return 0;

        int enIdx = 4, jaIdx = 5;
        var header = records[0];
        for (int i = 0; i < header.Count; i++)
        {
            var h = header[i].Trim();
            if (h.Equals("English", StringComparison.OrdinalIgnoreCase)) enIdx = i;
            else if (h.Equals("Japanese", StringComparison.OrdinalIgnoreCase)) jaIdx = i;
        }

        int n = 0;
        for (int r = 1; r < records.Count; r++)
        {
            var cols = records[r];
            if (cols.Count <= Math.Max(enIdx, jaIdx)) continue;
            var en = cols[enIdx].Replace("\r\n", "\n");
            var ja = cols[jaIdx].Replace("\r\n", "\n");
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(ja)) continue;
            _map[en] = ja;
            n++;
        }
        return n;
    }

    // RFC4180 相当: 引用符内のカンマ/改行/二重引用符("")に対応
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }
            if (c == '"') { inQuotes = true; i++; continue; }
            if (c == ',') { row.Add(field.ToString()); field.Clear(); i++; continue; }
            if (c == '\r') { i++; continue; }
            if (c == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) records.Add(row);
                row = new List<string>();
                i++; continue;
            }
            field.Append(c); i++;
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0) records.Add(row);
        }
        return records;
    }
}
