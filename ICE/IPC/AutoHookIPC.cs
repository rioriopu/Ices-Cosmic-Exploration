using ECommons.EzIpcManager;
using ECommons.Reflection;
using System.Threading.Tasks;

namespace ICE.IPC
{
    public class AutoHookIPC
    {
        public const string Name = "AutoHook";
        public const string Repo = "https://github.com/PunishXIV/AutoHook";
        public AutoHookIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);
        public bool Installed => Utils.HasPlugin(Name);
        public bool UpdatedPlugin()
        {
            // Really only need this for users, should probably add a way for dev plugin versions (ah) but :shrug:

            /*
            if (DalamudReflector.TryGetDalamudPlugin(Name, out var plogon, false, true))
            {
                if (plogon.GetType().Assembly.GetName().Version < new Version(6, 0, 0, 27))
                    return false;

                return true;
            }

            return false;
            */
            return true;
        }

        [EzIPC] private readonly Func<bool> GetPluginState;
        [EzIPC] private Action<bool> SetPluginState;

        [EzIPC] private readonly Func<bool> GetAutoStartFishing;
        [EzIPC] private Action<bool> SetAutoStartFishing;

        [EzIPC] public Action<bool> SetAutoGigState;
        [EzIPC] public Action<string> SetPreset;
        [EzIPC] public Action<string> SetPresetAutogig;
        [EzIPC] public Action<string> CreateAndSelectAnonymousPreset;
        [EzIPC] public Action<string> CreateAndSelectAnonymousFolder;
        [EzIPC] public Action<string> ImportAndSelectPreset;
        [EzIPC] public Action DeleteSelectedPreset;
        [EzIPC] public Action DeleteAllAnonymousPresets;
        // AutoHook 側の SwapBaitById は同期 bool を返す。Task<bool> と誤宣言していると EzIPC が毎回
        // Boolean→Task`1 の変換ログ(VRB「Could not convert Boolean to Task`1」)を出すため、実体に合わせる。
        // ※餌切替の挙動そのものは不変。ログノイズを消すだけの隔離した変更。
        [EzIPC] public Func<uint, bool> SwapBaitById;

        // swimbait(スイムベイト)専用。コスモ探査の改良コスモエサ等は通常餌用の SwapBaitById(item id)では
        // 装備できず内部NREになる。swimbait は「アイテムIDではなくインデックス(0〜2)」で選択する(AutoHook実装に準拠)。
        [EzIPC] public Func<byte, bool> SwapSwimbaitByIndex;

        /// <summary>
        /// AutoHook が今釣りに使うプリセット名(選択中の独自プリセット)を反射で読む。IPC に取得口が無いため。
        /// 戻り値 false = 読み取れなかった(AutoHook 未導入/内部構造の変更)。true で name が null なら未選択=Global Preset。
        /// 参照: AutoHook.Configuration.C.HookPresets.SelectedPreset.PresetName(AutoHook 6.0.2 系で確認)。
        /// </summary>
        public bool TryGetSelectedPresetName(out string name)
        {
            name = null;
            try
            {
                var hookPresets = GetHookPresets();
                if (hookPresets == null)
                    return false;
                var selected = hookPresets.GetFoP("SelectedPreset");
                name = selected?.GetFoP("PresetName") as string;
                return true;
            }
            catch (Exception ex)
            {
                ECommons.DalamudServices.Svc.Log.Debug($"[AutoHook] 選択中プリセットを読めませんでした: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// AutoHook の FishingPresets(Configuration.C.HookPresets)を反射で取り出す。無ければ null。
        /// 設定クラスは名前空間が版で変わる(6.0.2 系は AutoHook.Presets.Config.Configuration)ので型名だけで探す。
        /// </summary>
        private object GetHookPresets()
        {
            if (!DalamudReflector.TryGetDalamudPlugin(Name, out var plugin, false, true) || plugin == null)
                return null;
            var asm = plugin.GetType().Assembly;
            var cfgType = asm.GetType("AutoHook.Presets.Config.Configuration")
                          ?? asm.GetType("AutoHook.Configuration")
                          ?? asm.GetTypes().FirstOrDefault(t => t.Name == "Configuration" && t.GetProperty("C", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null);
            var cfg = cfgType?.GetProperty("C", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            return cfg?.GetFoP("HookPresets");
        }

        /// <summary>
        /// AutoHook の PresetList キャッシュを捨てさせる。
        /// AutoHook 6.0.2 系は SelectedPreset を「件数が変わった時だけ作り直すキャッシュ」から引くが、
        /// IPC の DeleteAllAnonymousPresets(1 件削除)と CreateAndSelectAnonymousPreset(1 件追加)はキャッシュを更新しないため、
        /// 件数が元に戻ると新しいプリセットが見つからず Global Preset で釣ってしまう(UI の一覧だけは「>」が付く)。
        /// 匿名プリセットの削除/投入の後に呼ぶ。反射で私有メソッド/フィールドを触るので、無ければ何もしない。
        /// </summary>
        public bool InvalidatePresetCache()
        {
            try
            {
                var hookPresets = GetHookPresets();
                if (hookPresets == null)
                    return false;
                var t = hookPresets.GetType();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                var method = t.GetMethod("InvalidatePresetListCache", flags);
                if (method != null)
                {
                    method.Invoke(hookPresets, null);
                    return true;
                }
                var cacheField = t.GetField("_presetListCache", flags);
                var countField = t.GetField("_presetListCacheCount", flags);
                if (cacheField == null && countField == null)
                    return false;
                cacheField?.SetValue(hookPresets, null);
                countField?.SetValue(hookPresets, -1);
                return true;
            }
            catch (Exception ex)
            {
                ECommons.DalamudServices.Svc.Log.Debug($"[AutoHook] プリセットキャッシュを無効化できませんでした: {ex.Message}");
                return false;
            }
        }

        public void Ah_State(bool state)
        {
            bool stateEnabled = GetPluginState();
            bool autoStartEnabled = GetAutoStartFishing();

            if (EzThrottler.Throttle("Applying autohook states"))
            {
                if (state)
                {
                    if (!stateEnabled)
                        SetPluginState(true);
                }

                if (!state)
                {
                    if (stateEnabled)
                        SetPluginState(false);
                }
            }
        }
    }
}
