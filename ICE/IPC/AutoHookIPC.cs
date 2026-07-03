using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ICE.IPC
{
    public class AutoHookIPC
    {
        public const string Name = "AutoHook";
        public const string Repo = "https://github.com/PunishXIV/AutoHook";
        public AutoHookIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);
        public bool Installed => Utils.HasPlugin(Name);

        [EzIPC] public Action<bool> SetPluginState;
        [EzIPC] public Action<bool> SetAutoGigState;
        [EzIPC] public Action<string> SetPreset;
        [EzIPC] public Action<string> SetPresetAutogig;
        [EzIPC] public Action<string> CreateAndSelectAnonymousPreset;
        [EzIPC] public Action<string> ImportAndSelectPreset;
        [EzIPC] public Action DeleteSelectedPreset;
        [EzIPC] public Action DeleteAllAnonymousPresets;
        // AutoHook 側の SwapBaitById は同期 bool を返す。従来 Task<bool> と誤宣言していたため、
        // EzIPC が Boolean→Task`1 の変換に失敗(ログ「Could not convert Boolean to Task`1」)し、
        // await 時に NRE、fire-and-forget でもスワップが正しく実行されずエサ切替が効かない原因になっていた。
        [EzIPC] public Func<uint, bool> SwapBaitById;
    }
}
