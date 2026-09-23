using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.ConfigFiles.Config;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_Gamba
    {
        public static readonly List<Gamba> DefaultGambaItems = new()
        {
            // Mounts
            new() { ItemId = 44505, Weight = 200, Type = GambaType.Mount },   // Vacuum Suit Identification Key
            new() { ItemId = 47973, Weight = 200, Type = GambaType.Mount },   // Warp Loader Identification Key
            new() { ItemId = 50441, Weight = 200, Type = GambaType.Mount },   // Volatile Gravity Vacuum Suit Identification Key
            new() { ItemId = 52267, Weight = 200, Type = GambaType.Mount },   // Cosmic Armored Weapon Beta Identification Key

            // Emotes
            new() { ItemId = 44509, Weight = 25, Type = GambaType.Emote },    // Ballroom Etiquette - Personal Perfection
            new() { ItemId = 46795, Weight = 25, Type = GambaType.Emote },    // Ballroom Etiquette - Anticipating Exertion

            // Outfits
            new() { ItemId = 47937, Weight = 50, Type = GambaType.Outfit },   // Cosmosuit Coffer
            new() { ItemId = 47095, Weight = 50, Type = GambaType.Outfit },   // Star Pilot Attire Coffer
            new() { ItemId = 50828, Weight = 50, Type = GambaType.Outfit },   // Powersuit Coffer
            new() { ItemId = 52605, Weight = 25, Type = GambaType.Outfit },   // Cosmic Operator's Attire Coffer

            // Minions
            new() { ItemId = 47966, Weight = 25, Type = GambaType.Minion },   // Micro Rover
            new() { ItemId = 46782, Weight = 25, Type = GambaType.Minion },   // Model Suit
            new() { ItemId = 50323, Weight = 25, Type = GambaType.Minion },   // Droningway
            new() { ItemId = 52275, Weight = 25, Type = GambaType.Minion },   // Lite-loader

            // Accessories
            new() { ItemId = 48154, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Tinted Sunglasses
            new() { ItemId = 48160, Weight = 5, Type = GambaType.Accessory }, // Loparasol
            new() { ItemId = 46840, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Scaevan Headgear
            new() { ItemId = 50458, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Holovisor
            new() { ItemId = 50455, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Holospecs
            new() { ItemId = 52449, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Wrap-around Sunglasses

            // Orchestration
            new() { ItemId = 48210, Weight = 0, Type = GambaType.Orchestrion }, // Stargazers Orchestrion Roll
            new() { ItemId = 48220, Weight = 0, Type = GambaType.Orchestrion }, // Echoes in the Distance Orchestrion Roll
            new() { ItemId = 48221, Weight = 0, Type = GambaType.Orchestrion }, // Close in the Distance (Instrumental) Orchestrion Roll
            new() { ItemId = 46155, Weight = 0, Type = GambaType.Orchestrion }, // Kaleidoscope Orchestrion Roll
            new() { ItemId = 50803, Weight = 0, Type = GambaType.Orchestrion }, // The Uncharted Way Orchestrion Roll
            new() { ItemId = 52359, Weight = 0, Type = GambaType.Orchestrion }, // Landscaping Orchestrion Roll
            new() { ItemId = 52648, Weight = 0, Type = GambaType.Orchestrion }, // Carrots of Brilliance Orchestrion Roll

            // Housing Items
            new() { ItemId = 23892, Weight = 0, Type = GambaType.Housing }, // Verdant Partition
            new() { ItemId = 48733, Weight = 0, Type = GambaType.Housing }, // Cosmotable
            new() { ItemId = 48734, Weight = 0, Type = GambaType.Housing }, // Cosmolamp
            new() { ItemId = 48136, Weight = 0, Type = GambaType.Housing }, // Drafting Table
            new() { ItemId = 32215, Weight = 0, Type = GambaType.Housing }, // Spring Meadow Partition
            new() { ItemId = 46175, Weight = 0, Type = GambaType.Housing }, // Portable Exoterminal
            new() { ItemId = 46174, Weight = 0, Type = GambaType.Housing }, // Cosmokitchen Partition
            new() { ItemId = 46173, Weight = 0, Type = GambaType.Housing }, // Cosmoseat
            new() { ItemId = 49849, Weight = 0, Type = GambaType.Housing }, // Imitation Cosmoportal
            new() { ItemId = 49850, Weight = 0, Type = GambaType.Housing }, // Cosmomodule Floor Panel
            new() { ItemId = 51277, Weight = 0, Type = GambaType.Housing }, // Cosmic Metal Shelf
            new() { ItemId = 51279, Weight = 0, Type = GambaType.Housing }, // Cosmic Partition
            new() { ItemId = 51278, Weight = 0, Type = GambaType.Housing }, // Cosmic Steps

            // Dyes
            new() { ItemId = 52255, Weight = 0, Type = GambaType.Dye }, // Wide Spectrum #1 Dye
            new() { ItemId = 52256, Weight = 0, Type = GambaType.Dye }, // Wide Spectrum #2 Dye

            // Materia
            new() { ItemId = 41762, Weight = 0, Type = GambaType.Materia }, // Gatherer's Guerdon Materia XI
            new() { ItemId = 41763, Weight = 0, Type = GambaType.Materia }, // Gatherer's Guile Materia XI
            new() { ItemId = 41764, Weight = 0, Type = GambaType.Materia }, // Gatherer's Grasp Materia XI
            new() { ItemId = 41765, Weight = 0, Type = GambaType.Materia }, // Craftsman's Competence Materia XI
            new() { ItemId = 41766, Weight = 0, Type = GambaType.Materia }, // Craftsman's Cunning Materia XI
            new() { ItemId = 41767, Weight = 0, Type = GambaType.Materia }, // Craftsman's Command Materia XI
            new() { ItemId = 41775, Weight = 0, Type = GambaType.Materia }, // Gatherer's Guerdon Materia XII
            new() { ItemId = 41776, Weight = 0, Type = GambaType.Materia }, // Gatherer's Guile Materia XII
            new() { ItemId = 41777, Weight = 0, Type = GambaType.Materia }, // Gatherer's Grasp Materia XII
            new() { ItemId = 41778, Weight = 0, Type = GambaType.Materia }, // Craftsman's Competence Materia XII
            new() { ItemId = 41779, Weight = 0, Type = GambaType.Materia }, // Craftsman's Cunning Materia XII
            new() { ItemId = 41780, Weight = 0, Type = GambaType.Materia }, // Craftsman's Command Materia XII

            // Other
            new() { ItemId = 43943, Weight = 0, Type = GambaType.Other }, // Cracked Prismaticrystal
            new() { ItemId = 43944, Weight = 0, Type = GambaType.Other }, // Cracked Novacrystal
            new() { ItemId = 28724, Weight = 0, Type = GambaType.Other }, // Crafter's Delineation
            new() { ItemId = 6141,  Weight = 0, Type = GambaType.Other }, // Cordial HQ
            new() { ItemId = 48158, Weight = 0, Type = GambaType.Other }, // Magicked Prism (Cosmic Exploration)
            new() { ItemId = 50450, Weight = 0, Type = GambaType.Other }, // Cosmic Barding
        };
        public static void EnsureGambaWeightsInitialized(bool force = false)
        {
            bool changed = false;
            if (force)
                C.GambaItemWeights.Clear();
            foreach (var item in DefaultGambaItems)
            {
                var existing = C.GambaItemWeights.FirstOrDefault(x => x.ItemId == item.ItemId);
                if (existing != null)
                {
                    // 既存エントリのカテゴリ(Type)は DefaultGambaItems の確定値へ同期する(Weight はユーザー設定を保持)。
                    // 以前 Other 等で自動登録された景品を正しいタブへ直すため。
                    if (existing.Type != item.Type) { existing.Type = item.Type; changed = true; }
                    continue;
                }
                C.GambaItemWeights.Add(new Gamba { ItemId = item.ItemId, Weight = item.Weight, Type = item.Type });
                changed = true;
            }
            if (changed)
                C.Save();
        }
        /// <summary>
        /// アイテムの種別から GambaType を推定する。
        /// まず ItemAction の種別(言語非依存: 853=ミニオン / 1322=マウント / 25183=オーケストリオン譜 / 20086=ファッションアクセサリー)で判定し、
        /// 次に ItemUICategory(81=Minion / 94=Orchestrion Roll / 55=Dye / 58=Materia / 57,65-82=家具 / 63=Other / 61=Miscellany)で判定する。
        /// 名前による補助推定は英語名で行う(日本語クライアントでも同じ判定になるように)。不明は Other。
        /// </summary>
        public static GambaType GuessGambaType(uint itemId)
        {
            try
            {
                if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var item))
                    return GambaType.Other;

                // ItemAction の種別値は Action 列(Action シートへの参照)の RowId に入っている
                int actionType = (int)(item.ItemAction.ValueNullable?.Action.RowId ?? 0);
                switch (actionType)
                {
                    case 853: return GambaType.Minion;
                    case 1322: return GambaType.Mount;
                    case 25183: return GambaType.Orchestrion;
                    case 20086: return GambaType.Accessory;
                }

                uint cat = item.ItemUICategory.RowId;
                string name = "";
                var enSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>(Dalamud.Game.ClientLanguage.English);
                if (enSheet != null && enSheet.TryGetRow(itemId, out var enItem))
                    name = enItem.Name.ExtractText() ?? "";
                if (string.IsNullOrEmpty(name))
                    name = ExcelItemHelper.GetName(itemId) ?? "";
                switch (cat)
                {
                    case 81: return GambaType.Minion;
                    case 94: return GambaType.Orchestrion;
                    case 55: return GambaType.Dye;
                    case 58: return GambaType.Materia;
                    // 家具系(室内外・装飾・テーブル・敷物・壁掛け・園芸 等)
                    case 57: case 65: case 66: case 67: case 69: case 70: case 71: case 72:
                    case 73: case 74: case 75: case 76: case 77: case 78: case 79: case 80: case 82:
                        return GambaType.Housing;
                    case 63: // Other: コスミックのマウントは「Identification Key」アイテム
                        return name.Contains("Identification Key") ? GambaType.Mount : GambaType.Other;
                    case 61: // Miscellany: Emote/Outfit/Accessory が混在するため名前で補助推定
                        if (name.Contains("Ballroom Etiquette")) return GambaType.Emote;
                        if (name.Contains("Coffer") || name.Contains("Attire")) return GambaType.Outfit;
                        if (name.Contains("The Faces We Wear") || name.Contains("Sunglasses") || name.Contains("Glasses")
                            || name.Contains("Visor") || name.Contains("Parasol") || name.Contains("Eyepatch"))
                            return GambaType.Accessory;
                        return GambaType.Other;
                    default: return GambaType.Other;
                }
            }
            catch { return GambaType.Other; }
        }

        /// <summary>
        /// 現在のガンブルの輪に出ているアイテムのうち、GambaItemWeights に未登録のものを自動登録する。
        /// 景品プールはシートに無くバイナリ内のため、輪を開いた時にしか取得できない。新景品を ID ハードコードなしで
        /// 設定UIに出すためのフォールバック。種別は ItemUICategory から推定し、Weight は 0 で開始する。
        /// </summary>
        private static void RegisterUnknownWheelItems(WKSLottery gamba)
        {
            bool changed = false;
            void TryAdd(uint id)
            {
                if (id == 0) return;
                if (C.GambaItemWeights.Any(x => x.ItemId == id)) return;
                var type = GuessGambaType(id);
                C.GambaItemWeights.Add(new Gamba { ItemId = id, Weight = 0, Type = type });
                changed = true;
                IceLogging.Info($"[Gamba] 未登録のホイールアイテムを自動登録: ItemId={id} (Type={type}, Weight=0)");
            }

            foreach (var item in gamba.LeftWheelItems) TryAdd((uint)item.itemId);
            foreach (var item in gamba.RightWheelItems) TryAdd((uint)item.itemId);

            if (changed)
                C.Save();
        }

        /// <summary>設定UIから: 開いているガンブルの輪(WKSLottery)を読み、未登録の景品をカテゴリ推定付きで登録する。
        /// 戻り値: (輪が開いていたか, 追加件数)。</summary>
        public static (bool wheelOpen, int added) ScanOpenWheel()
        {
            if (GenericHelpers.TryGetAddonMaster<WKSLottery>("WKSLottery", out var gamba) && gamba.IsAddonReady)
            {
                int before = C.GambaItemWeights.Count;
                RegisterUnknownWheelItems(gamba);
                return (true, C.GambaItemWeights.Count - before);
            }
            return (false, 0);
        }

        /// <summary>設定UIから: 登録済み全景品のカテゴリ(Type)を ItemUICategory から一括で再判定する。戻り値: 変更件数。</summary>
        public static int RecategorizeAll()
        {
            int changed = 0;
            foreach (var g in C.GambaItemWeights)
            {
                var t = GuessGambaType(g.ItemId);
                if (g.Type != t)
                {
                    IceLogging.Info($"[Gamba] カテゴリ再判定: ItemId={g.ItemId} {g.Type}→{t}");
                    g.Type = t;
                    changed++;
                }
            }
            if (changed > 0)
                C.Save();
            return changed;
        }

        public static void Enqueue()
        {
            EnsureGambaWeightsInitialized();
            if (GenericHelpers.TryGetAddonMaster<WKSLottery>("WKSLottery", out var gamba) && gamba.IsAddonReady)
            {
                P.TaskManager.EnqueueMulti
                    (
                        new(GamblingTime, "Time to go gambling!", Utils.TaskConfig),
                        new(CloseTalk, "Closing the talk window"),
                        new(() => SchedulerMain.State = IceState.Idle)
                    );
            }
            else
            {
                // If this is the case, then we're here to initalize the gamba
                P.TaskManager.EnqueueMulti
                    (
                        new(Gamba_PathTo, "Pathing to the gamba NPC"),
                        new(TalkToGambaNpc, "Talk to the Gamba NPC"),
                        new(SelectGamba, "Selecting the options to go to gamba"),
                        new(GamblingTime, "Time to go gambling!", Utils.TaskConfig),
                        new(CloseTalk, "Closing the talk window")
                    );
            }
        }
        private static bool? Gamba_PathTo()
        {
            string handle = "Task_Gamba: PathTo";
            var zoneId = Player.Territory;

            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Gamba, out var npcEntry))
            {
                Vector3 randomPos = NpcData.GetRandomPointInCircle(npcEntry.Location_Circle, 0.5f);
                if (!Task_NavmeshMove.Task_NavTo(randomPos, distance: 5, npcLoc: npcEntry.Location_Npc).Value)
                {
                    if (EzThrottler.Throttle("Repair move message", 1000))
                        IceLogging.Verbose($"Pathing to repair NPC. Current distance: {Player.DistanceTo(npcEntry.Location_Npc)}", handle);
                }
                else
                {
                    IceLogging.Debug("We're close enough to the repair npc! Continuing on", handle);
                    return true;
                }
            }
            else
            {
                if (EzThrottler.Throttle("Error message: NPC", 5000))
                    IceLogging.Error("Hey! We don't have this npc coded yet, which means I forgot bout it, could you let me know\n" +
                                     $"Planet Territory ID: {Player.Territory.RowId}", handle);
            }

            return false;
        }
        private static bool? TalkToGambaNpc()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var selectString) && selectString.IsAddonReady)
            {
                IceLogging.Info("We've gotten to selecting the npc dialog (woo!). Selecting gamba");
                return true;
            }
            else if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Closing Talk Window", 250))
                    talk.Click();
            }
            else
            {
                if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Gamba, out var gambaNpc))
                {
                    Utils.TryGetObjectByDataId(gambaNpc.NpcId, out var researchNpc);
                    if (EzThrottler.Throttle("Interacting with gambaNpc!"))
                    {
                        Utils.TargetgameObject(researchNpc);
                        Utils.InteractWithObject(researchNpc);
                    }
                }
            }

            return false;
        }
        private static bool? SelectGamba()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectIconString>("SelectIconString", out var iconString) && iconString.IsAddonReady)
            {
                if (EzThrottler.Throttle("Selecting Materia Selection"))
                {
                    var select = iconString.Entries[0];
                    IceLogging.Debug($"Selecting: {select.Text}");
                    select.Select();
                }
            }
            else if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var selectString) && selectString.IsAddonReady)
            {
                if (EzThrottler.Throttle("Selecting yes to gamba"))
                {
                    selectString.Entries[0].Select();
                }
            }
            else if (GenericHelpers.TryGetAddonMaster<WKSLottery>("WKSLottery", out var gamba) && gamba.IsAddonReady)
            {
                return true;
            }

            return false;
        }
        private static unsafe bool? GamblingTime()
        {
            string tag = "Gambling Time Task";

            if (GenericHelpers.TryGetAddonMaster<WKSLottery>("WKSLottery", out var gamba) && gamba.IsAddonReady)
            {
                // 輪に出た未登録アイテム(新景品)を自動登録する。ID ハードコード不要で将来の追加にも追随する。
                RegisterUnknownWheelItems(gamba);

                var territory = Player.Territory.RowId;
                if (!CosmicMoonRegistry.TryGetPlanetCreditItemId(territory, out var itemId))
                    return false;

                PlayerHelper.GetItemCount(itemId, out var credits);

                bool confirmEnabled, leftWheelEnabled, rightWheelEnabled;
                unsafe
                {
                    confirmEnabled = gamba.SpinWheelButton->IsEnabled;
                    leftWheelEnabled = gamba.WheelLeftButton->IsEnabled;
                    rightWheelEnabled = gamba.WheelRightButton->IsEnabled;
                }

                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var select) && select.IsAddonReady)
                {
                    if (credits >= 1000 + C.GambaCreditsMinimum)
                        select.Yes();
                    else
                        select.No();
                }
                else if (confirmEnabled)
                    gamba.ConfirmButton();
                else if (leftWheelEnabled || rightWheelEnabled)
                {
                    float leftWeight = gamba.LeftWheelItems.Sum(item => C.GambaItemWeights.FirstOrDefault(x => x.ItemId == item.itemId)?.Weight ?? 0);
                    float rightWeight = gamba.RightWheelItems.Sum(item => C.GambaItemWeights.FirstOrDefault(x => x.ItemId == item.itemId)?.Weight ?? 0);

                    if (C.GambaPreferSmallerWheel)
                    {
                        leftWeight = gamba.LeftWheelItems.Length > 0 ? leftWeight / gamba.LeftWheelItems.Length : 0;
                        rightWeight = gamba.RightWheelItems.Length > 0 ? rightWeight / gamba.RightWheelItems.Length : 0;

                        if (leftWeight == rightWeight && leftWeight > 0)
                        {
                            leftWeight += 1.0f / Math.Max(1, gamba.LeftWheelItems.Length);
                            rightWeight += 1.0f / Math.Max(1, gamba.RightWheelItems.Length);
                        }
                    }

                    if (gamba.LeftWheelItems.Length == 0)
                    {
                        IceLogging.Info($"Found a pure stellar mission gamba. Choosing left wheel", tag);
                        SelectWheelLeft(gamba);
                    }
                    else if (gamba.RightWheelItems.Length == 0)
                    {
                        IceLogging.Info($"Found a pure stellar mission gamba. Choosing right wheel", tag);
                        SelectWheelRight(gamba);
                    }
                    else if (leftWeight > rightWeight)
                    {
                        IceLogging.Info($"[Gamba] First wheel is better with total weight: {leftWeight}");
                        SelectWheelLeft(gamba);
                    }
                    else if (rightWeight > leftWeight)
                    {
                        IceLogging.Info($"[Gamba] Second wheel is better with total weight: {rightWeight}");
                        SelectWheelRight(gamba);
                    }
                    else
                    {
                        IceLogging.Info("[Gamba] Both wheels are equal in weight. Randomly selecting one.");
                        if (new Random().Next(2) == 0)
                            SelectWheelLeft(gamba);
                        else
                            SelectWheelRight(gamba);
                    }
                }

                return false;
            }
            else
            {
                return true;
            }

        }
        private static unsafe bool HasEnoughCredits()
        {
            var territory = Player.Territory.RowId;
            if (!CosmicMoonRegistry.TryGetPlanetCreditItemId(territory, out var itemId))
                return false;

            PlayerHelper.GetItemCount(itemId, out var credits);
            return credits >= 1000;
        }
        private static bool? CloseTalk()
        {
            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Closing Talk Window", 250))
                    talk.Click();
                return false;
            }
            else
            {
                return true;
            }
        }
        public static unsafe void SelectWheelLeft(WKSLottery gamba)
        {
            gamba.WheelLeftButton->Flags = 327936U; // Checked, Enabled, Selected
            gamba.WheelRightButton->Flags = 65792U; // Not Checked, Enabled, Not Selected
            IceLogging.Debug($"[Gamba] Selecting Left Wheel");
        }
        public static unsafe void SelectWheelRight(WKSLottery gamba)
        {
            gamba.WheelLeftButton->Flags = 65792U; // Not Checked, Enabled, Not Selected
            gamba.WheelRightButton->Flags = 327936U; // Checked, Enabled, Selected
            IceLogging.Debug($"[Gamba] Selecting Right Wheel");
        }
        public static bool BigBangGamba()
        {
            // Big Bang Tickets are earned from doing the fates... and this kind fucks with things? 
            // Name of the item is "Bing Bang Fortune (Planet Name)

            return false;
        }
    }
}
