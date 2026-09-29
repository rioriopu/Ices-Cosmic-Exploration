using ECommons.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ICE.ConfigFiles
{
    /// <summary>
    /// 設定の保存。
    /// 複数のゲームクライアント(同じ Dalamud フォルダ)で ICE を同時に動かすと、各クライアントがメモリ上の設定を
    /// 丸ごと書き出すため「最後に保存したクライアントの内容」が勝ち、他のクライアントで入れたチェックが
    /// Reload 後に外れて見える(2026-09-30 ユーザー報告: ステラーステータスの自動解除が外れる)。
    /// そこで保存時は「このインスタンスが読み書きした時点(基準)からの差分」だけをディスクの現在の内容に重ねて書く。
    /// 差分の判定は JSON のプロパティ単位(入れ子のオブジェクト/辞書はキー単位)。失敗したら従来どおり丸ごと保存する。
    /// </summary>
    public static class EzConfigExtensions
    {
        private static readonly object _saveLock = new object();
        private static CancellationTokenSource? _saveCts;

        // このインスタンスが最後に読み込み/保存した時点の設定(JSON)。差分の基準
        private static JObject? _baseline;
        // 同じ PC 上の別クライアントとの読み書きを直列化する(同一ユーザーセッション内)
        private static readonly Mutex _crossProcess = new(false, @"Local\ICE_DefaultConfig_Save");
        private static bool _warnedFallback;

        private static readonly JsonSerializerSettings ParseSettings = new()
        {
            // 日時文字列を DateTime に解釈させない(再出力で表記が変わって偽の差分になるのを防ぐ)
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
        };

        /// <summary>読み込み直後に呼ぶ。今のメモリ上の設定を差分の基準にする。</summary>
        public static void CaptureBaseline()
        {
            try
            {
                if (EzConfig.Config == null) return;
                _baseline = ParseJson(EzConfig.DefaultSerializationFactory.Serialize(EzConfig.Config, true));
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"設定の基準スナップショットを作れませんでした(従来の保存にします): {ex.Message}");
                _baseline = null;
            }
        }

        public static async Task SaveAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    SaveMerged();
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Failed to save EzConfig \n" +
                        $"{ex}");
                    throw;
                }
            }).ConfigureAwait(false);
        }

        public static void SaveDebounced(int delayMs = 500)
        {
            lock (_saveLock)
            {
                _saveCts?.Cancel();
                _saveCts = new CancellationTokenSource();
                var cts = _saveCts;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, cts.Token);
                        await SaveAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Newer save cancelled this one
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error($"Failed to save EzConfig: {ex}");
                    }
                });
            }
        }

        /// <summary>
        /// 差分マージ保存。基準が無い/失敗した場合は EzConfig.Save()(丸ごと保存)に戻す。
        /// </summary>
        public static void SaveMerged()
        {
            var config = EzConfig.Config;
            if (config == null) return;
            if (_baseline == null)
            {
                EzConfig.Save();
                return;
            }

            try
            {
                lock (config)
                {
                    var current = ParseJson(EzConfig.DefaultSerializationFactory.Serialize(config, true));
                    var path = EzConfig.DefaultConfigurationFileName;

                    bool locked = false;
                    try
                    {
                        try { locked = _crossProcess.WaitOne(TimeSpan.FromSeconds(5)); }
                        catch (AbandonedMutexException) { locked = true; }

                        JObject? disk = null;
                        if (File.Exists(path))
                        {
                            try { disk = ParseJson(File.ReadAllText(path, Encoding.UTF8)); }
                            catch (Exception ex) { PluginLog.Warning($"設定ファイルを読み直せなかったため、メモリ上の設定で上書きします: {ex.Message}"); }
                        }

                        JObject merged;
                        if (disk == null || JToken.DeepEquals(disk, _baseline))
                        {
                            // 他所からの変更なし(前回このインスタンスが書いた内容のまま) → そのまま書く
                            merged = current;
                        }
                        else
                        {
                            // 他のクライアントが書き換えている → その内容を土台に、こちらの変更分だけを重ねる
                            merged = (JObject)disk.DeepClone();
                            int applied = ApplyChanges(_baseline, current, merged);
                            PluginLog.Debug($"[Config] 別クライアントの変更を保持したまま保存しました(こちらの変更 {applied} 箇所を反映)");
                        }

                        WriteAtomic(path, merged.ToString(Formatting.Indented));
                        // 次回の基準は「このインスタンスのメモリ上の内容」。ディスク側の他所の値を基準に含めると、
                        // 次の保存でこちらの古い値を"変更"と誤認して書き戻してしまう
                        _baseline = current;
                    }
                    finally
                    {
                        if (locked) { try { _crossProcess.ReleaseMutex(); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_warnedFallback)
                {
                    _warnedFallback = true;
                    PluginLog.Warning($"設定の差分マージ保存に失敗したため、従来の丸ごと保存にします: {ex}");
                }
                EzConfig.Save();
                try { CaptureBaseline(); } catch { }
            }
        }

        private static JObject ParseJson(string text)
            => JsonConvert.DeserializeObject<JObject>(text, ParseSettings) ?? throw new InvalidDataException("設定 JSON が空です");

        /// <summary>
        /// baseline → current で変わった箇所だけを target に反映する。オブジェクト(辞書含む)は入れ子で辿り、
        /// 配列や値は丸ごと置き換える。current から消えたプロパティは target からも消す。戻り値: 反映した箇所の数。
        /// </summary>
        private static int ApplyChanges(JObject baseline, JObject current, JObject target)
        {
            int applied = 0;
            foreach (var prop in current.Properties())
            {
                var baseVal = baseline[prop.Name];
                var curVal = prop.Value;
                if (baseVal != null && JToken.DeepEquals(baseVal, curVal))
                    continue; // このインスタンスでは変えていない

                if (curVal is JObject curObj && baseVal is JObject baseObj && target[prop.Name] is JObject targetObj)
                {
                    applied += ApplyChanges(baseObj, curObj, targetObj);
                }
                else
                {
                    target[prop.Name] = curVal.DeepClone();
                    applied++;
                }
            }
            var removed = new List<string>();
            foreach (var prop in baseline.Properties())
                if (current[prop.Name] == null && target[prop.Name] != null)
                    removed.Add(prop.Name);
            foreach (var name in removed)
            {
                target.Remove(name);
                applied++;
            }
            return applied;
        }

        // ECommons と同じ「.new に書いてから差し替える」方式(書き込み途中で落ちても元ファイルを壊さない)
        private static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".new";
            if (File.Exists(tmp))
            {
                var moved = $"{tmp}.{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
                try { File.Move(tmp, moved); } catch { File.Delete(tmp); }
            }
            File.WriteAllText(tmp, text, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
