using ECommons.Automation.UIInput;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ICE.Utilities.Cosmic_Helper;

// ECommons を NuGet パッケージ参照(本家同様)へ切り替えたことに伴う互換シム。
// NuGet 版 ECommons(3.2.1.6) の AddonMaster.WKSMission には「マスターシップミッションタブ(ボタンID 20)」を
// 押すための MastershipMissions() / MastershipMissionsButton が存在しない(7.51 で追加されたタブのため)。
// 本来 ECommons 側にあった実装(ClickButtonIfEnabled で当該ボタンを押すだけ)を ICE 側で等価に補完する。
// 使用箇所が WKSMission インスタンスのメソッド呼び出し(missionInfo.MastershipMissions())なので拡張メソッドで提供する。
internal static class WKSMissionCompat
{
    /// <summary>
    /// マスターシップミッションタブ(ボタンID 20)をクリックする。
    /// 本家 ECommons の AddonMasterBase.ClickButtonIfEnabled と等価の処理。
    /// </summary>
    public static unsafe bool MastershipMissions(this AddonMaster.WKSMission m)
    {
        var button = m.Addon->GetComponentButtonById(20);
        if (button != null && button->IsEnabled && button->AtkResNode->IsVisible())
        {
            button->ClickAddonButton(m.Base);
            return true;
        }
        return false;
    }
}
