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
