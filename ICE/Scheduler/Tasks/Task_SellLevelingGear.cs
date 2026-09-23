using Dalamud.Game.ClientState.Keys;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.Utilities.LevelingGearShop;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// レベリング装備の自動売却。ゴッドギスで買える Lv10〜95 のレベリング装備(購入ボタンと同じ選び方、
    /// 現在ジョブの種類=クラフター/ギャザラーの全ジョブ分)のうち、アーマリーチェストにある NQ 品だけを
    /// ゴッドギスへ移動して売る。HQ 品は売らない。マテリア化できるかどうかは無視して売る。
    /// 流れ: 計画(BuildPlan) → 確認 → NPC へ移動 → 店舗を開く → 各アイテムのコンテキストメニューから「売却」→ 反映確認 → … → 閉じる。
    /// </summary>
    internal static class Task_SellLevelingGear
    {
        public class SellEntry
        {
            public uint ItemId;
            public string Name = "";
            public InventoryType Container;
            public int Slot;
            public GearSlot GearSlot;
            public byte LevelEquip;
            public uint Price;   // 売却価格(PriceLow)
            public bool Sold;
            public bool Failed;
        }

        public class SellPlan
        {
            public uint Job;
            public string JobName = "";
            public bool IsGatherer;
            public uint NpcId;
            public List<SellEntry> Items = new();
            public long TotalGil;
            public string Error = "";
            public int SoldCount;
            public long EarnedGil;
            public bool Aborted;
        }

        public static SellPlan Current { get; private set; }
        public static bool Running { get; private set; }
        public static bool IsJapanese => Task_BuyLevelingGear.IsJapanese;

        /// <summary>ゴッドギスで買える Lv10〜95 のレベリング装備(購入ボタンと同じ選び方、全段階、種類の全ジョブ分)の ItemId 集合。</summary>
        public static HashSet<uint> LadderItemIds(bool gatherer, NpcShopData data)
        {
            var jobs = gatherer ? CosmicHelper.GatheringJobList : CosmicHelper.CrafterJobList;
            var set = new HashSet<uint>();
            foreach (var job in jobs)
            {
                var candidates = data.AllItems
                    .Where(x => x.Jobs.Contains(job) && KindMatchesJob(x.Kind, job) && Task_BuyLevelingGear.TargetSlots.Contains(x.Slot))
                    .ToList();
                foreach (int lv in Task_BuyLevelingGear.Steps)
                    foreach (var slot in Task_BuyLevelingGear.TargetSlots)
                    {
                        var best = candidates
                            .Where(x => x.Slot == slot && x.LevelEquip <= lv)
                            .OrderByDescending(x => x.LevelEquip)
                            .ThenByDescending(x => x.ItemLevel)
                            .ThenBy(x => x.Price)
                            .FirstOrDefault();
                        if (best != null) set.Add(best.ItemId);
                    }
            }
            return set;
        }

        /// <summary>アーマリーチェストにある売却対象(NQ のみ)を集める。</summary>
        public static unsafe SellPlan BuildPlan(uint job)
        {
            string jobName = CosmicHelper.GetJobName(job);
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(job, out var cj) && !string.IsNullOrEmpty(cj.Name.ExtractText()))
                jobName = cj.Name.ExtractText();
            var plan = new SellPlan { Job = job, JobName = jobName, IsGatherer = CosmicHelper.GatheringJobList.Contains(job) };
            Current = plan;

            if (!CosmicHelper.CrafterJobList.Contains(job) && !CosmicHelper.GatheringJobList.Contains(job))
            {
                plan.Error = IsJapanese ? "クラフター/ギャザラーのジョブで実行してください" : "Switch to a crafter or gatherer job first";
                return plan;
            }
            plan.NpcId = CurrentVendorNpcId();
            if (plan.NpcId == 0)
            {
                plan.Error = IsJapanese ? "この惑星には装備ベンダーが登録されていません" : "No gear vendor is registered for this planet";
                return plan;
            }

            var data = Resolve(plan.NpcId);
            var ids = LadderItemIds(plan.IsGatherer, data);
            if (ids.Count == 0)
            {
                plan.Error = IsJapanese ? "ベンダーの品揃えを取得できませんでした" : "Could not read the vendor's inventory";
                return plan;
            }

            var itemSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var inv = InventoryManager.Instance();
            foreach (var slot in Task_BuyLevelingGear.TargetSlots)
            {
                var type = ArmouryOf(slot);
                var container = inv->GetInventoryContainer(type);
                if (container == null) continue;
                for (int i = 0; i < container->Size; i++)
                {
                    var it = container->GetInventorySlot(i);
                    if (it == null || it->ItemId == 0 || !ids.Contains(it->ItemId)) continue;
                    if (it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)) continue; // HQ は売らない
                    if (!itemSheet.TryGetRow(it->ItemId, out var row)) continue;
                    plan.Items.Add(new SellEntry
                    {
                        ItemId = it->ItemId,
                        Name = row.Name.ExtractText(),
                        Container = type,
                        Slot = i,
                        GearSlot = slot,
                        LevelEquip = row.LevelEquip,
                        Price = row.PriceLow,
                    });
                    plan.TotalGil += row.PriceLow;
                }
            }
            plan.Items = plan.Items.OrderBy(x => x.GearSlot).ThenBy(x => x.LevelEquip).ToList();
            return plan;
        }

        // ------------------------------------------------------------------ 実行 ----

        private static SellPlan _plan;
        private static DateTime _start = DateTime.MinValue;
        private static DateTime _lastProgress = DateTime.MinValue;
        private static int _menuDepth = -1;
        private static int _menuCloses = 0;
        private static string _lastMenuSig = "";
        private static SellEntry _verifying;
        private static int _verifyBefore;
        private static DateTime _verifyAt = DateTime.MinValue;
        private static int _retry = 0;
        private static DateTime _contextOpenedAt = DateTime.MinValue;
        private const double NoProgressAbortSeconds = 120;
        private const int MaxMenuCloses = 8;
        private const double VerifySeconds = 8;

        public static void Enqueue(SellPlan plan)
        {
            _plan = plan;
            Running = true;
            plan.SoldCount = 0;
            plan.EarnedGil = 0;
            plan.Aborted = false;
            foreach (var e in plan.Items) { e.Sold = false; e.Failed = false; }
            _menuDepth = -1;
            _menuCloses = 0;
            _lastMenuSig = "";
            _verifying = null;
            _retry = 0;
            _lastProgress = DateTime.Now;

            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の売却を開始します: {plan.Items.Count} 点（中止: Stop ボタン / /ice stop / Esc）"
                : $"Selling leveling gear: {plan.Items.Count} items (abort: Stop button / /ice stop / Esc)", "[I.C.E.]");

            P.TaskManager.Enqueue(() => Task_BuyLevelingGear.ReturnToHub(), "Leveling gear: Stellar Return to the hub", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => Task_Repair.Repair_PathTo(), "Leveling gear: walking to the vendor", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => { _start = DateTime.Now; return true; });
            P.TaskManager.Enqueue(() => SellAll(), "Leveling gear: selling", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => { _start = DateTime.Now; return true; });
            P.TaskManager.Enqueue(() => CloseAllMenus(), "Leveling gear: closing menus", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => Finish(), "Leveling gear: finished");
        }

        /// <summary>緊急停止。タスクを全て破棄し、開いているメニュー/店舗を閉じる。</summary>
        public static unsafe void Abort(string reason)
        {
            if (!Running && _plan == null)
                return;
            if (_plan != null) _plan.Aborted = true;
            Running = false;
            P.TaskManager.Abort();
            try
            {
                if (GenericHelpers.TryGetAddonMaster<ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu>("ContextMenu", out var cm) && cm.IsAddonReady)
                    ECommons.Automation.Callback.Fire(cm.Base, true, -1);
                if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
                    ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                if (TryGetMenu(out _, out _, out var close))
                    close();
            }
            catch { }
            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の売却を中止しました（{reason}）: 売却済み {_plan?.SoldCount ?? 0} 点 / {_plan?.EarnedGil ?? 0:N0} ギル"
                : $"Leveling gear sale aborted ({reason}): sold {_plan?.SoldCount ?? 0} items / {_plan?.EarnedGil ?? 0:N0} gil", "[I.C.E.]");
        }

        private static string Normalize(string s) => (s ?? "").Replace(" ", "").Replace("　", "").Trim();

        private static int FindEntry(List<string> texts, string name)
        {
            string n = Normalize(name);
            if (n.Length == 0) return -1;
            for (int i = 0; i < texts.Count; i++)
            {
                string t = Normalize(texts[i]);
                if (t.Length == 0) continue;
                if (t == n || t.Contains(n) || (n.Contains(t) && t.Length >= 4))
                    return i;
            }
            return -1;
        }

        private static unsafe bool TryGetMenu(out List<string> texts, out Action<int> select, out Action close)
        {
            texts = null; select = null; close = null;
            if (GenericHelpers.TryGetAddonMaster<SelectIconString>("SelectIconString", out var sis) && sis.IsAddonReady)
            {
                var entries = sis.Entries;
                texts = entries.Select(e => { try { return e.Text ?? ""; } catch { return ""; } }).ToList();
                select = i => entries[i].Select();
                close = () => ECommons.Automation.Callback.Fire(sis.Base, true, -1);
                return true;
            }
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
            {
                var entries = ss.Entries;
                texts = entries.Select(e => { try { return e.Text ?? ""; } catch { return ""; } }).ToList();
                select = i => entries[i].Select();
                close = () => ECommons.Automation.Callback.Fire(ss.Base, true, -1);
                return true;
            }
            return false;
        }

        private static unsafe int CountOwned(uint itemId)
            => InventoryManager.Instance()->GetInventoryItemCount(itemId, false, true, true);

        // 指定スロットに今もその NQ アイテムがあるか
        private static unsafe bool StillThere(SellEntry e)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(e.Container);
            if (container == null) return false;
            var it = container->GetInventorySlot(e.Slot);
            return it != null && it->ItemId == e.ItemId && !it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        }

        // 売却に使う店舗(どの GilShop でもよい)。NPC メニュー直下の「アイテムの購入」があればそれ、無ければ最初の店舗。
        private static ShopInfo SellShop()
        {
            if (!TryGetCached(_plan.NpcId, out var data) || data.Shops.Count == 0)
                return null;
            return data.Shops.FirstOrDefault(s => s.ShopId != 0 && string.IsNullOrEmpty(s.MenuName) && s.MenuIndex >= 0)
                ?? data.Shops.FirstOrDefault(s => s.ShopId != 0);
        }

        private static unsafe bool? SellAll()
        {
            string tag = "[Leveling Gear Sell]";
            if (_plan == null || _plan.Aborted || !Running)
                return true;
            if (Svc.KeyState[VirtualKey.ESCAPE])
            {
                Abort("Esc");
                return true;
            }
            if (!Player.Available)
                return false;

            var next = _plan.Items.FirstOrDefault(e => !e.Sold && !e.Failed);
            if (next == null)
                return true;

            if ((DateTime.Now - _lastProgress).TotalSeconds > NoProgressAbortSeconds)
            {
                Abort(IsJapanese ? $"{NoProgressAbortSeconds:F0}秒以上売却が進まない" : $"no progress for {NoProgressAbortSeconds:F0}s");
                return true;
            }
            if (_menuCloses > MaxMenuCloses)
            {
                IceLogging.Error($"メニューを {_menuCloses} 回閉じても店舗を開けません。最後のメニュー: [{_lastMenuSig}]", tag);
                Abort(IsJapanese ? "メニューを辿れない" : "could not navigate the vendor menu");
                return true;
            }

            // 売却の反映待ち
            if (_verifying != null)
            {
                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yn) && yn.IsAddonReady)
                {
                    if (EzThrottler.Throttle("LGearSell yes", 300)) yn.Yes();
                    return false;
                }
                if (CountOwned(_verifying.ItemId) < _verifyBefore || !StillThere(_verifying))
                {
                    _verifying.Sold = true;
                    _plan.SoldCount++;
                    _plan.EarnedGil += _verifying.Price;
                    _lastProgress = DateTime.Now;
                    IceLogging.Info($"売却完了: {_verifying.Name} ({_verifying.Price:N0}g) [{_plan.SoldCount}/{_plan.Items.Count}]", tag);
                    _verifying = null;
                    _retry = 0;
                    return false;
                }
                if (GenericHelpers.TryGetAddonMaster<ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu>("ContextMenu", out var cmv) && cmv.IsAddonReady)
                {
                    // 「売却」を選んだのにメニューが残っている → 閉じてやり直す
                    if ((DateTime.Now - _verifyAt).TotalSeconds > 3 && EzThrottler.Throttle("LGearSell close ctx", 1000))
                        ECommons.Automation.Callback.Fire(cmv.Base, true, -1);
                    return false;
                }
                if ((DateTime.Now - _verifyAt).TotalSeconds > VerifySeconds)
                {
                    _verifying = null;
                    _retry++;
                    if (_retry >= 2)
                    {
                        IceLogging.Error($"{next.Name} を売却できませんでした(2回試行)。飛ばします", tag);
                        next.Failed = true;
                        _retry = 0;
                    }
                }
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGearSell yes", 300)) yesno.Yes();
                return false;
            }

            // コンテキストメニューが開いている → 「売却」を選ぶ
            if (GenericHelpers.TryGetAddonMaster<ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu>("ContextMenu", out var cm) && cm.IsAddonReady)
            {
                var entries = cm.Entries;
                var texts = entries.Select(e => { try { return e.Text ?? ""; } catch { return ""; } }).ToList();
                int idx = -1;
                for (int i = 0; i < texts.Count; i++)
                {
                    var t = texts[i];
                    if (t.Contains("売却") || t.IndexOf("sell", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                {
                    if (EzThrottler.Throttle("LGearSell select", 700))
                    {
                        _verifyBefore = CountOwned(next.ItemId);
                        _verifyAt = DateTime.Now;
                        _verifying = next;
                        IceLogging.Info($"売却: {next.Name} Lv{next.LevelEquip} {next.Price:N0}g (menu '{texts[idx]}')", tag);
                        if (!entries[idx].Select())
                            IceLogging.Warning($"「{texts[idx]}」を選択できませんでした(無効な項目)", tag);
                    }
                    return false;
                }
                if ((DateTime.Now - _contextOpenedAt).TotalSeconds > 2 && EzThrottler.Throttle("LGearSell close ctx", 1000))
                {
                    IceLogging.Error($"コンテキストメニューに「売却」がありません: [{string.Join(" | ", texts)}]。{next.Name} を飛ばします", tag);
                    ECommons.Automation.Callback.Fire(cm.Base, true, -1);
                    next.Failed = true;
                }
                return false;
            }

            // 店舗が開いている → 次のアイテムのコンテキストメニューを開く
            if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
            {
                _menuDepth = 2;
                if (!StillThere(next))
                {
                    IceLogging.Warning($"{next.Name} がアーマリーチェストの元の位置にありません。飛ばします", tag);
                    next.Failed = true;
                    return false;
                }
                if (EzThrottler.Throttle("LGearSell open ctx", 1200))
                {
                    var agent = AgentInventoryContext.Instance();
                    if (agent == null) return false;
                    uint addonId = 0;
                    var proxy = ShopEventHandler.AgentProxy.Instance();
                    if (proxy != null) addonId = proxy->AddonId;
                    if (addonId == 0)
                    {
                        var shopAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Shop);
                        if (shopAgent != null) addonId = shopAgent->GetAddonId();
                    }
                    if (addonId == 0)
                    {
                        var invAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
                        if (invAgent != null) addonId = invAgent->GetAddonId();
                    }
                    IceLogging.Debug($"コンテキストメニューを開く: {next.Name} {next.Container}#{next.Slot} addon={addonId}", tag);
                    agent->OpenForItemSlot(next.Container, next.Slot, 0, addonId);
                    _contextOpenedAt = DateTime.Now;
                }
                return false;
            }

            // 店舗を開くためにメニューを辿る
            var sellShop = SellShop();
            if (sellShop == null)
            {
                IceLogging.Error("売却に使う店舗が見つかりません", tag);
                _plan.Aborted = true;
                return true;
            }
            if (TryGetMenu(out var mtexts, out var select, out var close))
            {
                string sig = string.Join(" | ", mtexts);
                if (sig != _lastMenuSig)
                {
                    _lastMenuSig = sig;
                    IceLogging.Info($"メニュー(depth={_menuDepth}, {mtexts.Count}項目): [{sig}]", tag);
                }
                if (_menuDepth < 0)
                {
                    if (EzThrottler.Throttle("LGearSell close menu", 1000)) { close(); _menuCloses++; }
                    return false;
                }
                int i = FindEntry(mtexts, sellShop.Name);
                int nextDepth = 2;
                if (i < 0 && !string.IsNullOrEmpty(sellShop.MenuName))
                {
                    i = FindEntry(mtexts, sellShop.MenuName);
                    nextDepth = 1;
                }
                if (i < 0)
                {
                    if (_menuDepth == 0 && sellShop.MenuIndex >= 0 && sellShop.MenuIndex < mtexts.Count)
                    {
                        i = sellShop.MenuIndex;
                        nextDepth = sellShop.ShopIndex >= 0 ? 1 : 2;
                    }
                    else if (_menuDepth == 1 && sellShop.ShopIndex >= 0 && sellShop.ShopIndex < mtexts.Count)
                    {
                        i = sellShop.ShopIndex;
                        nextDepth = 2;
                    }
                }
                if (i >= 0)
                {
                    if (EzThrottler.Throttle("LGearSell select menu", 700))
                    {
                        IceLogging.Info($"メニュー選択: #{i} '{mtexts[i]}' (depth {_menuDepth}→{nextDepth})", tag);
                        select(i);
                        _menuDepth = nextDepth;
                    }
                    return false;
                }
                if (EzThrottler.Throttle("LGearSell close menu", 1500))
                {
                    IceLogging.Info($"目的の項目が無いメニューを閉じます(depth={_menuDepth}): [{sig}]", tag);
                    close();
                    _menuCloses++;
                    _menuDepth = -1;
                }
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGearSell talk", 100)) talk.Click();
                return false;
            }

            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Repair, out var npc))
            {
                if (Player.DistanceTo(npc.Location_Npc) > 6f)
                {
                    Task_NavmeshMove.Task_NavTo(npc.Location_Circle, false, 3f, npcLoc: npc.Location_Npc);
                    return false;
                }
                if (Player.Mounted)
                {
                    Utils.Dismount();
                    return false;
                }
                if (!GenericHelpers.IsOccupied() && Utils.TryGetObjectByDataId(npc.NpcId, out var obj) && obj != null
                    && EzThrottler.Throttle("LGearSell interact", 1200))
                {
                    _menuDepth = 0;
                    Utils.TargetgameObject(obj);
                    Utils.InteractWithObject(obj);
                }
            }
            else
            {
                IceLogging.Error("装備ベンダーが登録されていない惑星です", tag);
                _plan.Aborted = true;
                return true;
            }
            return false;
        }

        private static unsafe bool? CloseAllMenus()
        {
            if (_plan == null || _plan.Aborted || !Running)
                return true;
            if ((DateTime.Now - _start).TotalSeconds > 10)
                return true;
            if (GenericHelpers.TryGetAddonMaster<ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu>("ContextMenu", out var cm) && cm.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGearSell close ctx", 800)) ECommons.Automation.Callback.Fire(cm.Base, true, -1);
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGearSell close shop", 800)) ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                return false;
            }
            if (TryGetMenu(out _, out _, out var close))
            {
                if (EzThrottler.Throttle("LGearSell close menu", 800)) close();
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGearSell talk", 100)) talk.Click();
                return false;
            }
            return !GenericHelpers.IsOccupied();
        }

        private static bool? Finish()
        {
            if (_plan == null)
                return true;
            bool wasRunning = Running;
            Running = false;
            if (!wasRunning || _plan.Aborted)
                return true;
            int failed = _plan.Items.Count(e => !e.Sold);
            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の売却が終わりました: {_plan.SoldCount} 点 / {_plan.EarnedGil:N0} ギル" + (failed > 0 ? $"（未売却 {failed} 点）" : "")
                : $"Leveling gear sale finished: {_plan.SoldCount} items / {_plan.EarnedGil:N0} gil" + (failed > 0 ? $" ({failed} not sold)" : ""), "[I.C.E.]");
            return true;
        }
    }
}
