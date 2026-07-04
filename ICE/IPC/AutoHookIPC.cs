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
        // AutoHook 側の SwapBaitById は同期 bool を返す。従来 Task<bool> と誤宣言していたため、EzIPC が毎回
        // Boolean→Task`1 の変換ログ(VRB「Could not convert Boolean to Task`1」)を出していた。実体に合わせて修正。
        // ※餌切替の挙動そのものは不変。ノイズを消すだけの隔離した変更。
        [EzIPC] public Func<uint, bool> SwapBaitById;
        // swimbait(スイムベイト)専用。コスモ探査の改良コスモエサ等は swimbait で、通常餌用の SwapBaitById(item id)では
        // 装備できず内部NREになる。swimbait は「アイテムIDではなくインデックス(0〜2)」で選択する(AutoHook実装に準拠)。
        [EzIPC] public Func<byte, bool> SwapSwimbaitByIndex;
    }
}
