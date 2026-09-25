using ECommons.EzIpcManager;
using ICE.Utilities.Cosmic_Helper;

namespace ICE.IPC
{
    /// <summary>
    /// AutoRetainer への窓口。ベンチャーが回収できるか、回収中か、を聞くのに使う。
    /// AutoRetainer 側は InternalName + ".PluginState" を前置詞にして登録している
    /// (実名: AutoRetainer.PluginState.IsBusy 等)。前置詞を落とすと全て IpcNotReadyError になる。
    /// </summary>
    public class AutoRetainerIPC
    {
        public const string Name = "AutoRetainer";

        // 例外は握らずに受け取り(SafeWrapper.None)、呼び出し側で「分からない(Unknown)」として扱う。
        // 握って false を返すと「回収できない」と誤読するため。
        public AutoRetainerIPC() => EzIPC.Init(this, $"{Name}.PluginState", SafeWrapper.None);

        public bool Installed => Utils.HasPlugin(Name);

        /// <summary>AutoRetainer が処理中か(ベンチャーの受け取り・再出発も含む)</summary>
        [EzIPC] public Func<bool> IsBusy;

        /// <summary>このキャラクターに、いま回収できるベンチャーを持つリテイナーが居るか(AutoRetainer で有効にしたリテイナーのみ)</summary>
        [EzIPC] public Func<bool> AreAnyRetainersAvailableForCurrentChara;

        /// <summary>処理を中断させる(こちらから画面を閉じる前に呼ぶ)</summary>
        [EzIPC] public Action AbortAllTasks;

        /// <summary>ベンチャーを回収できるかの判定結果。「分からない」を「無い」と解釈しないための 3 値</summary>
        public enum VentureState
        {
            Unknown,
            Collectable,
            None,
        }

        public bool TryIsBusy(out bool busy)
        {
            busy = false;
            if (!Installed || IsBusy == null)
                return false;
            try
            {
                busy = IsBusy();
                return true;
            }
            catch (Exception ex)
            {
                if (EzThrottler.Throttle("AutoRetainer IsBusy error", 10000))
                    IceLogging.Debug($"AutoRetainer.IsBusy の呼び出しに失敗: {ex.Message}", "[AutoRetainer IPC]");
                return false;
            }
        }

        public VentureState CheckCollectableVenture()
        {
            if (!Installed || AreAnyRetainersAvailableForCurrentChara == null)
                return VentureState.Unknown;
            try
            {
                return AreAnyRetainersAvailableForCurrentChara() ? VentureState.Collectable : VentureState.None;
            }
            catch (Exception ex)
            {
                if (EzThrottler.Throttle("AutoRetainer venture error", 10000))
                    IceLogging.Debug($"AutoRetainer.AreAnyRetainersAvailableForCurrentChara の呼び出しに失敗: {ex.Message}", "[AutoRetainer IPC]");
                return VentureState.Unknown;
            }
        }

        public void TryAbort()
        {
            if (!Installed || AbortAllTasks == null)
                return;
            try
            {
                AbortAllTasks();
            }
            catch (Exception ex)
            {
                IceLogging.Debug($"AutoRetainer.AbortAllTasks の呼び出しに失敗: {ex.Message}", "[AutoRetainer IPC]");
            }
        }
    }
}
