using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ICE.Utilities.Cosmic_Helper;

namespace ICE.Utilities;

/// <summary>
/// ICE の開始(Start)・停止(Stop)のときに、視点をキャラの背後へ戻す。
/// 角度を直接書き換えず、ゲーム自身の視点リセット(Camera.ResetAngles、仮想関数)を呼ぶ。
/// vnavmesh の「移動方向へカメラを合わせる」が動いた後に、視点が変な向きのまま残らないようにするため(2026-10-13 ユーザー指示)。
/// </summary>
internal static unsafe class CameraReset
{
    private const string Tag = "[Camera]";

    /// <summary>設定が有効なら、数フレーム後に視点をリセットする(画面のスレッドから呼ぶ)。</summary>
    public static void Request(string why)
    {
        if (!C.ResetCameraOnStartStop)
            return;
        // vnavmesh のカメラ追従は、経路が無くなった次のフレームに外れる。止めた直後に戻すと上書きされることがあるので少し待つ
        GenericHelpers.Safe(() => Svc.Framework.RunOnTick(() => Apply(why), delayTicks: 3));
    }

    private static void Apply(string why)
    {
        try
        {
            // 会話・カットシーン・エリア移動中・グループポーズ中は、ゲームがカメラを動かしているので触らない
            if (!Player.Available || !PlayerHelper.IsScreenReady() || Svc.ClientState.IsGPosing)
                return;
            var manager = CameraManager.Instance();
            // 通常の三人称カメラ(0 番)が有効なときだけ
            if (manager == null || manager->ActiveCameraIndex != 0 || manager->Camera == null)
                return;
            manager->Camera->ResetAngles();
            IceLogging.Debug($"視点をリセットしました({why})", Tag);
        }
        catch (Exception ex)
        {
            IceLogging.Error($"視点のリセットに失敗しました({why}): {ex.Message}", Tag);
        }
    }
}
