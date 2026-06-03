using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ICE.Utilities.Cosmic_Helper;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Handlers
{
    internal static class GearsetHandler // Borrowed from Artisan
    {
        internal unsafe static void TaskClassChange(Job job)
        {
            if (job == Player.Job || !EzThrottler.Throttle("Gearset", 250) || Player.IsBusy)
                return;
            var gearsets = RaptureGearsetModule.Instance();
            foreach (ref var gs in gearsets->Entries)
            {
                if (!RaptureGearsetModule.Instance()->IsValidGearset(gs.Id)) continue;
                if ((Job)gs.ClassJob == job)
                {
                    if (gs.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing))
                    {
                        // 「代用しますか?」ダイアログが出ている → 「代用する」を押して確定し、即return。
                        // ここでreturnしないと下の無条件EquipGearsetが代用ダイアログを再度開き、ジョブ変更後に誰も閉じず画面に残る。
                        if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var select) && select.IsAddonReady)
                        {
                            select.Yes();
                            return;
                        }
                        // ダイアログ未表示 → 下の共通EquipGearsetでダイアログを出す(二重装備しない)
                    }

                    var result = gearsets->EquipGearset(gs.Id);
                    IceLogging.Debug($"Tried to equip gearset {gs.Id} for {job}, result={result}, flags={gs.Flags}");
                    return;
                }
            }
            return;
        }
    }
}