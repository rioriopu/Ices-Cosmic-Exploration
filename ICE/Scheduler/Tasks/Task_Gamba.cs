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

            // Emotes
            new() { ItemId = 44509, Weight = 25, Type = GambaType.Emote },    // Ballroom Etiquette - Personal Perfection
            new() { ItemId = 46795, Weight = 25, Type = GambaType.Emote },    // Ballroom Etiquette - Anticipating Exertion

            // Outfits
            new() { ItemId = 47937, Weight = 50, Type = GambaType.Outfit },   // Cosmosuit Coffer
            new() { ItemId = 47095, Weight = 50, Type = GambaType.Outfit },   // Star Pilot Attire Coffer
            new() { ItemId = 50828, Weight = 50, Type = GambaType.Outfit },   // Powersuit Coffer

            // Minions
            new() { ItemId = 47966, Weight = 25, Type = GambaType.Minion },   // Micro Rover
            new() { ItemId = 46782, Weight = 25, Type = GambaType.Minion },   // Model Suit
            new() { ItemId = 50323, Weight = 25, Type = GambaType.Minion },   // Droningway

            // Accessories
            new() { ItemId = 48154, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Tinted Sunglasses
            new() { ItemId = 48160, Weight = 5, Type = GambaType.Accessory }, // Loparasol
            new() { ItemId = 46840, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Scaevan Headgear
            new() { ItemId = 50458, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Holovisor
            new() { ItemId = 50455, Weight = 5, Type = GambaType.Accessory }, // The Faces We Wear - Holospecs

            // Orchestration
            new() { ItemId = 48210, Weight = 0, Type = GambaType.Orchestrion }, // Stargazers Orchestrion Roll
            new() { ItemId = 48220, Weight = 0, Type = GambaType.Orchestrion }, // Echoes in the Distance Orchestrion Roll
            new() { ItemId = 48221, Weight = 0, Type = GambaType.Orchestrion }, // Close in the Distance (Instrumental) Orchestrion Roll
            new() { ItemId = 46155, Weight = 0, Type = GambaType.Orchestrion }, // Kaleidoscope Orchestrion Roll
            new() { ItemId = 50803, Weight = 0, Type = GambaType.Orchestrion }, // The Uncharted Way Orchestrion Roll

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

            // === 7.51 Auxesia の新規Cosmic系コスメ(ルーレット景品候補) ===
            // 景品プールは静的データに無くライブの輪からしか確定できないため、命名/カテゴリから抽出した候補を事前登録。
            // 実際の輪に出れば OnWheelOpened が確認・補完する。輪に出ない品はWeight関係なく未使用(無害)。
            new() { ItemId = 52262, Weight = 200, Type = GambaType.Mount },   // Rocket Punch Identification Key
            new() { ItemId = 52267, Weight = 200, Type = GambaType.Mount },   // Cosmic Armored Weapon Beta Identification Key
            new() { ItemId = 52268, Weight = 200, Type = GambaType.Mount },   // Cosmic Predator Identification Key
            new() { ItemId = 52271, Weight = 200, Type = GambaType.Mount },   // Excavating Vacuum Suit Identification Key
            new() { ItemId = 52272, Weight = 200, Type = GambaType.Mount },   // Carbide Grey Warp Loader Identification Key
            new() { ItemId = 52292, Weight = 25,  Type = GambaType.Emote },   // Ballroom Etiquette - Dignified Derision
            new() { ItemId = 52293, Weight = 25,  Type = GambaType.Emote },   // Ballroom Etiquette - Orchestral Operations
            new() { ItemId = 52294, Weight = 25,  Type = GambaType.Emote },   // Ballroom Etiquette - Trial by Taco
            new() { ItemId = 52605, Weight = 50,  Type = GambaType.Outfit },  // Cosmic Operator's Attire Coffer
            new() { ItemId = 51276, Weight = 0,   Type = GambaType.Housing }, // Auxesian Tower Replica
            new() { ItemId = 51277, Weight = 0,   Type = GambaType.Housing }, // Cosmic Metal Shelf
            new() { ItemId = 51278, Weight = 0,   Type = GambaType.Housing }, // Cosmic Steps
            new() { ItemId = 51279, Weight = 0,   Type = GambaType.Housing }, // Cosmic Partition
            new() { ItemId = 51280, Weight = 0,   Type = GambaType.Housing }, // Auxesian Waygate
            new() { ItemId = 51281, Weight = 0,   Type = GambaType.Housing }, // Auxesian Waylight
        };
        public static void EnsureGambaWeightsInitialized(bool force = false)
        {
            bool changed = false;
            if (force)
                C.GambaItemWeights.Clear();
            foreach (var item in DefaultGambaItems)
            {
                if (C.GambaItemWeights.Any(x => x.ItemId == item.ItemId))
                    continue;
                C.GambaItemWeights.Add(new Gamba { ItemId = item.ItemId, Weight = item.Weight, Type = item.Type });
                changed = true;
            }
            if (changed)
                C.Save();
        }

        /// <summary>
        /// アイテムのItemUICategoryからGambaTypeを推定する。Minion/Orchestrion/Dye/Materia/Housingは一意に判定可能。
        /// Mount(63=Other,「Identification Key」)とEmote/Outfit/Accessory(61=Miscellany)は名前で補助推定し、不明はOther。
        /// (ItemUICategory: 81=Minion / 94=Orchestrion Roll / 55=Dye / 58=Materia / 57,65-82=家具 / 63=Other / 61=Miscellany)
        /// </summary>
        public static GambaType GuessGambaType(uint itemId)
        {
            try
            {
                if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var item))
                    return GambaType.Other;
                uint cat = item.ItemUICategory.RowId;
                string name = ExcelItemHelper.GetName(itemId) ?? "";
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
                    case 63: // Other: Cosmicのマウントは「Identification Key」アイテム
                        return name.Contains("Identification Key") ? GambaType.Mount : GambaType.Other;
                    case 61: // Miscellany: Emote/Outfit/Accessory が混在。名前で補助推定
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
        /// 現在のガンバホイールに出ているアイテムのうち、GambaItemWeightsに未登録のものを自動登録する。
        /// Auxesia等の新景品をIDハードコードなしで設定UIに出すためのフォールバック。
        /// ItemUICategoryから種別を推定してカテゴリを付ける(7.51以降の新景品も正しく分類)。Weightは0で開始。
        /// 既知アイテムは DefaultGambaItems 側で正しいカテゴリ・初期Weight付きで登録される。
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

        /// <summary>設定UIから: 現在開いているガンバホイール(WKSLottery)を読み、未登録の景品をカテゴリ推定付きで登録する。
        /// 戻り値: (輪が開いていたか, 追加件数)。輪を開いた状態で押すと、その時点の景品が設定に反映される。</summary>
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

        /// <summary>設定UIから: 登録済み全景品のカテゴリ(Type)を ItemUICategory から一括再判定する。
        /// 以前 Type=Other で自動登録された景品や、誤カテゴリを正しい種別へ振り直す。戻り値: 変更件数。</summary>
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

            if (NpcData.MoonNpcs[Player.Territory.RowId].TryGetValue(NpcData.NpcType.Gamba, out var npcEntry))
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
                if (NpcData.MoonNpcs[Player.Territory.RowId].TryGetValue(NpcData.NpcType.Gamba, out var gambaNpc))
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
                // ホイールに出た未登録アイテム(Auxesia等の新景品)を自動登録。IDハードコード不要で将来も追随。
                RegisterUnknownWheelItems(gamba);

                var territory = Player.Territory.RowId;
                if (!CosmicHelper.PlanetCreditInfo.TryGetValue(territory, out var itemId))
                    return true; // 未対応惑星では通貨が引けないので何もしない
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
            var itemId = CosmicHelper.PlanetCreditInfo[territory];

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
