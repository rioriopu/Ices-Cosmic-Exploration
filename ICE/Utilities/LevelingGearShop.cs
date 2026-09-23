using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICE.Utilities;

/// <summary>
/// レベリング装備の購入元(拠点のギル装備ベンダー=ゴッドギス/Godgyth)の品揃えを取得する。
/// ゲームデータ(ENpcBase → ENpcData のハンドラ → GilShop → GilShopItem)を辿って静的に解決するので、
/// NPC に話しかけなくても取得できる。ハンドラは直接の GilShop のほか、TopicSelect/PreHandler/CustomTalk
/// (Lv1〜/Lv21〜 などの階層メニュー)を経由する場合があるため再帰的に辿る。
/// 実際に開いているショップ(ShopEventHandler)からの実測取り込みも備え、静的データの検証に使う。
/// </summary>
public static class LevelingGearShop
{
    // ENpcData のハンドラ ID は上位 16bit が種別(FFXIVClientStructs EventHandlerContent)
    private const ushort HandlerGilShop = 0x0004;
    private const ushort HandlerCustomTalk = 0x000B;
    private const ushort HandlerTopicSelect = 0x0032;
    private const ushort HandlerPreHandler = 0x0036;

    /// <summary>装備部位。アーマリーチェストの格納先と1対1で対応する。</summary>
    public enum GearSlot { Unknown, MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Ring }

    /// <summary>
    /// 装備の用途。防具/アクセサリは「クラフター向け」「ギャザラー向け」がどちらも全 DoH/DoL で装備できるため、
    /// 基本パラメータ(作業精度/加工精度/CP か 獲得力/識質力/GP か)で見分ける。
    /// </summary>
    public enum GearKind { Neutral, Crafter, Gatherer, Both }

    public class ShopGearItem
    {
        public uint ItemId { get; set; }
        public string Name { get; set; } = "";
        public byte LevelEquip { get; set; }
        public uint ItemLevel { get; set; }
        public GearSlot Slot { get; set; }
        public GearKind Kind { get; set; }
        public uint Price { get; set; }          // シート上のギル価格(PriceMid)。実測値は RuntimePrice
        public int RuntimePrice { get; set; } = -1; // 開いているショップから取り込んだ実売価格(未取得は -1)
        public bool IsHQ { get; set; }
        public List<uint> Jobs { get; set; } = new(); // 装備できるジョブ(8〜18 のクラフター/ギャザラーのみ)
        public string Bracket { get; set; } = "";    // 装備Lvから求めたレベル帯(Lv1〜/Lv21〜/…、表示用)
        public uint ShopId { get; set; }
        public string ShopName { get; set; } = "";   // GilShop 名(NPC メニューの店舗名と一致)
        public string MenuName { get; set; } = "";   // この店舗へ入る階層メニュー(TopicSelect)名。空なら NPC メニュー直下
        [JsonIgnore] public int ArmouryCount { get; set; }   // アーマリーチェスト内の所持数(実行時に更新)
        [JsonIgnore] public bool IsEquipped { get; set; }    // 現在装備中か(実行時に更新)
    }

    public class ShopInfo
    {
        public uint ShopId { get; set; }
        public string Name { get; set; } = "";
        public string MenuName { get; set; } = "";   // 階層メニュー(TopicSelect)名。NPC メニュー直下なら空
        public string Path { get; set; } = "";       // NPC からこのショップへ辿った経路(デバッグ用)
        public List<ShopGearItem> Items { get; set; } = new();
    }

    public class NpcShopData
    {
        public uint NpcId { get; set; }
        public string NpcName { get; set; } = "";
        public uint TerritoryId { get; set; }
        public DateTime ResolvedAt { get; set; }
        public List<ShopInfo> Shops { get; set; } = new();
        public IEnumerable<ShopGearItem> AllItems => Shops.SelectMany(s => s.Items);
    }

    private static readonly Dictionary<uint, NpcShopData> _cache = new();
    public static string LastMessage { get; private set; } = "";

    public static bool TryGetCached(uint npcId, out NpcShopData data) => _cache.TryGetValue(npcId, out data);

    /// <summary>現在の惑星のギル装備ベンダー(NpcData の Repair | Gil Gear Vendor)の NPC ID を返す。無ければ 0。</summary>
    public static uint CurrentVendorNpcId()
        => NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Repair, out var npc) ? npc.NpcId : 0;

    /// <summary>NPC の品揃えをゲームデータから解決してキャッシュする。</summary>
    public static NpcShopData Resolve(uint npcId, bool force = false)
    {
        if (!force && _cache.TryGetValue(npcId, out var cached))
            return cached;

        var data = new NpcShopData { NpcId = npcId, TerritoryId = Player.Territory.RowId, ResolvedAt = DateTime.Now };
        if (NpcData.TryGetMoon(Player.Territory.RowId, out var npcs))
            data.NpcName = npcs.Values.FirstOrDefault(n => n.NpcId == npcId)?.Name ?? "";

        var visited = new HashSet<uint>();
        if (Svc.Data.GetExcelSheet<ENpcBase>().TryGetRow(npcId, out var npcBase))
        {
            // ENpcData は 32 要素すべてを見る(先頭が 0 でも後ろにショップが入っている NPC がある)
            foreach (var handlerRef in npcBase.ENpcData)
            {
                if (handlerRef.RowId == 0) continue;
                InspectHandler(data, handlerRef.RowId, "ENpcData", "", visited, 0);
            }
            LastMessage = $"{data.NpcName}[{npcId}]: ショップ {data.Shops.Count} 件 / 装備 {data.AllItems.Count()} 件を取得";
        }
        else
        {
            LastMessage = $"ENpcBase に NPC {npcId} が見つかりません";
        }

        UpdateOwnership(data);
        _cache[npcId] = data;
        IceLogging.Info(LastMessage, "[LevelingGearShop]");
        foreach (var shop in data.Shops)
            IceLogging.Info($"  shop {shop.ShopId} '{shop.Name}' via {shop.Path}: {shop.Items.Count} 件", "[LevelingGearShop]");
        return data;
    }

    // ハンドラ ID を種別ごとに辿り、GilShop に到達したら品揃えを登録する。循環参照対策で深さ上限あり。
    private static void InspectHandler(NpcShopData data, uint handler, string path, string menuName, HashSet<uint> visited, int depth)
    {
        if (handler == 0 || depth > 4 || !visited.Add(handler))
            return;

        switch ((ushort)(handler >> 16))
        {
            case HandlerGilShop:
                AddGilShop(data, handler, path, menuName);
                break;

            case HandlerTopicSelect:
                if (Svc.Data.GetExcelSheet<TopicSelect>().TryGetRow(handler, out var topic))
                {
                    string topicName = topic.Name.ExtractText();
                    foreach (var shopRef in topic.Shop)
                        InspectHandler(data, shopRef.RowId, $"{path} > TopicSelect '{topicName}'", topicName, visited, depth + 1);
                }
                break;

            case HandlerPreHandler:
                if (Svc.Data.GetExcelSheet<PreHandler>().TryGetRow(handler, out var pre))
                    InspectHandler(data, pre.Target.RowId, $"{path} > PreHandler", menuName, visited, depth + 1);
                break;

            case HandlerCustomTalk:
                if (Svc.Data.GetExcelSheet<CustomTalk>().TryGetRow(handler, out var talk))
                {
                    string talkPath = $"{path} > CustomTalk '{talk.Name.ExtractText()}'";
                    // 階層メニューは CustomTalkNestHandlers か Script の引数で GilShop を参照していることがある
                    if (Svc.Data.GetSubrowExcelSheet<CustomTalkNestHandlers>().TryGetRow(handler, out var nest))
                        foreach (var n in nest)
                            InspectHandler(data, n.NestHandler.RowId, talkPath, menuName, visited, depth + 1);
                    foreach (var script in talk.Script)
                        if ((ushort)(script.ScriptArg >> 16) is HandlerGilShop or HandlerTopicSelect or HandlerPreHandler)
                            InspectHandler(data, script.ScriptArg, talkPath, menuName, visited, depth + 1);
                    if (talk.SpecialLinks.RowId != 0)
                        InspectHandler(data, talk.SpecialLinks.RowId, talkPath, menuName, visited, depth + 1);
                }
                break;
        }
    }

    private static void AddGilShop(NpcShopData data, uint shopId, string path, string menuName)
    {
        var shop = new ShopInfo { ShopId = shopId, Path = path, MenuName = menuName };
        if (Svc.Data.GetExcelSheet<GilShop>().TryGetRow(shopId, out var gilShop))
            shop.Name = gilShop.Name.ExtractText();

        if (Svc.Data.GetSubrowExcelSheet<GilShopItem>().TryGetRow(shopId, out var rows))
        {
            foreach (var row in rows)
            {
                if (!row.Item.IsValid) continue;
                var item = row.Item.Value;
                var slot = ToSlot(item.EquipSlotCategory.ValueNullable);
                if (slot == GearSlot.Unknown) continue; // 装備品以外(触媒・食事など)は対象外

                var jobs = CosmicJobs(item.ClassJobCategory.ValueNullable);
                if (jobs.Count == 0) continue;         // クラフター/ギャザラーが装備できない物は対象外

                shop.Items.Add(new ShopGearItem
                {
                    ItemId = item.RowId,
                    Name = item.Name.ExtractText(),
                    LevelEquip = item.LevelEquip,
                    ItemLevel = item.LevelItem.RowId,
                    Slot = slot,
                    Kind = KindOf(item),
                    Price = item.PriceMid,
                    IsHQ = row.IsHQ,
                    Jobs = jobs,
                    Bracket = BracketOf(item.LevelEquip),
                    ShopId = shopId,
                    ShopName = shop.Name,
                    MenuName = menuName,
                });
            }
        }

        if (shop.Items.Count > 0)
            data.Shops.Add(shop);
    }

    /// <summary>ショップの階層メニュー表記に合わせたレベル帯。</summary>
    public static string BracketOf(int levelEquip) => levelEquip switch
    {
        >= 81 => "Lv81~",
        >= 61 => "Lv61~",
        >= 41 => "Lv41~",
        >= 21 => "Lv21~",
        _ => "Lv1~",
    };

    private static GearSlot ToSlot(EquipSlotCategory? cat)
    {
        if (cat is not { } c) return GearSlot.Unknown;
        if (c.MainHand == 1) return GearSlot.MainHand;
        if (c.OffHand == 1) return GearSlot.OffHand;
        if (c.Head == 1) return GearSlot.Head;
        if (c.Body == 1) return GearSlot.Body;
        if (c.Gloves == 1) return GearSlot.Hands;
        if (c.Legs == 1) return GearSlot.Legs;
        if (c.Feet == 1) return GearSlot.Feet;
        if (c.Ears == 1) return GearSlot.Ears;
        if (c.Neck == 1) return GearSlot.Neck;
        if (c.Wrists == 1) return GearSlot.Wrists;
        if (c.FingerL == 1 || c.FingerR == 1) return GearSlot.Ring;
        return GearSlot.Unknown;
    }

    // 基本パラメータ(BaseParam: 70=作業精度 71=加工精度 11=CP / 72=獲得力 73=識質力 10=GP)から用途を判定する
    private static GearKind KindOf(Item item)
    {
        bool crafter = false, gatherer = false;
        foreach (var p in item.BaseParam)
        {
            if (p.RowId is 70 or 71 or 11) crafter = true;
            if (p.RowId is 72 or 73 or 10) gatherer = true;
        }
        return crafter && gatherer ? GearKind.Both : crafter ? GearKind.Crafter : gatherer ? GearKind.Gatherer : GearKind.Neutral;
    }

    /// <summary>ジョブに合った用途か(クラフターにはクラフター向け/中立、ギャザラーにはギャザラー向け/中立)。</summary>
    public static bool KindMatchesJob(GearKind kind, uint jobId)
    {
        bool isGatherer = CosmicHelper.GatheringJobList.Contains(jobId);
        return kind is GearKind.Neutral or GearKind.Both || (isGatherer ? kind == GearKind.Gatherer : kind == GearKind.Crafter);
    }

    // ClassJobCategory からクラフター/ギャザラー(8〜18)のうち装備できるジョブを列挙する
    private static List<uint> CosmicJobs(ClassJobCategory? cat)
    {
        var list = new List<uint>();
        if (cat is not { } c) return list;
        if (c.CRP) list.Add(8);
        if (c.BSM) list.Add(9);
        if (c.ARM) list.Add(10);
        if (c.GSM) list.Add(11);
        if (c.LTW) list.Add(12);
        if (c.WVR) list.Add(13);
        if (c.ALC) list.Add(14);
        if (c.CUL) list.Add(15);
        if (c.MIN) list.Add(16);
        if (c.BTN) list.Add(17);
        if (c.FSH) list.Add(18);
        return list;
    }

    /// <summary>部位 → アーマリーチェストのコンテナ。</summary>
    public static InventoryType ArmouryOf(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => InventoryType.ArmoryMainHand,
        GearSlot.OffHand => InventoryType.ArmoryOffHand,
        GearSlot.Head => InventoryType.ArmoryHead,
        GearSlot.Body => InventoryType.ArmoryBody,
        GearSlot.Hands => InventoryType.ArmoryHands,
        GearSlot.Legs => InventoryType.ArmoryLegs,
        GearSlot.Feet => InventoryType.ArmoryFeets,
        GearSlot.Ears => InventoryType.ArmoryEar,
        GearSlot.Neck => InventoryType.ArmoryNeck,
        GearSlot.Wrists => InventoryType.ArmoryWrist,
        GearSlot.Ring => InventoryType.ArmoryRings,
        _ => InventoryType.Invalid,
    };

    public static string SlotNameJp(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => "主道具",
        GearSlot.OffHand => "副道具",
        GearSlot.Head => "頭",
        GearSlot.Body => "胴",
        GearSlot.Hands => "腕",
        GearSlot.Legs => "脚",
        GearSlot.Feet => "足",
        GearSlot.Ears => "耳",
        GearSlot.Neck => "首",
        GearSlot.Wrists => "腕輪",
        GearSlot.Ring => "指",
        _ => "?",
    };

    /// <summary>アーマリーチェストの該当部位にあるアイテム数(NQ/HQ 問わず)。</summary>
    public static unsafe int CountInArmoury(uint itemId, GearSlot slot)
    {
        var type = ArmouryOf(slot);
        if (type == InventoryType.Invalid) return 0;
        var container = InventoryManager.Instance()->GetInventoryContainer(type);
        if (container == null) return 0;
        int count = 0;
        for (int i = 0; i < container->Size; i++)
        {
            var it = container->GetInventorySlot(i);
            if (it != null && it->ItemId == itemId) count += (int)it->Quantity;
        }
        return count;
    }

    /// <summary>所持品(バッグ 1〜4)にあるアイテム数。アーマリーが満杯だと購入品はバッグに入るため、重複購入の判定に含める。</summary>
    public static unsafe int CountInBags(uint itemId)
    {
        int count = 0;
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if (container == null) continue;
            for (int i = 0; i < container->Size; i++)
            {
                var it = container->GetInventorySlot(i);
                if (it != null && it->ItemId == itemId) count += (int)it->Quantity;
            }
        }
        return count;
    }

    /// <summary>所持ギル。</summary>
    public static unsafe long GetGil() => InventoryManager.Instance()->GetGil();

    /// <summary>アーマリー/バッグ/装備中のいずれかに持っているか。</summary>
    public static bool IsOwned(ShopGearItem item)
        => item.ArmouryCount > 0 || item.IsEquipped || CountInBags(item.ItemId) > 0;

    public static unsafe bool IsEquipped(uint itemId)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null) return false;
        for (int i = 0; i < container->Size; i++)
        {
            var it = container->GetInventorySlot(i);
            if (it != null && it->ItemId == itemId) return true;
        }
        return false;
    }

    /// <summary>アーマリーチェストの該当部位の空き枠数。</summary>
    public static unsafe int ArmouryFreeSlots(GearSlot slot)
    {
        var type = ArmouryOf(slot);
        if (type == InventoryType.Invalid) return 0;
        var container = InventoryManager.Instance()->GetInventoryContainer(type);
        if (container == null) return 0;
        int free = 0;
        for (int i = 0; i < container->Size; i++)
        {
            var it = container->GetInventorySlot(i);
            if (it == null || it->ItemId == 0) free++;
        }
        return free;
    }

    /// <summary>各アイテムの所持状況(アーマリーチェスト数/装備中)を更新する。</summary>
    public static void UpdateOwnership(NpcShopData data)
    {
        if (!Player.Available) return;
        foreach (var item in data.AllItems)
        {
            item.ArmouryCount = CountInArmoury(item.ItemId, item.Slot);
            item.IsEquipped = IsEquipped(item.ItemId);
        }
    }

    /// <summary>
    /// 開いているショップ(ShopEventHandler)の品目を読み、静的データの実売価格を埋める。
    /// 静的解決で拾えなかったアイテムはショップ名 "(実測)" として追加する。戻り値: 読めた品目数(ショップ未開なら -1)。
    /// </summary>
    public static unsafe int CaptureOpenShop(NpcShopData data)
    {
        var proxy = ShopEventHandler.AgentProxy.Instance();
        if (proxy == null || proxy->Handler == null)
            return -1;

        var handler = proxy->Handler;
        var runtime = data.Shops.FirstOrDefault(s => s.ShopId == 0);
        var itemSheet = Svc.Data.GetExcelSheet<Item>();
        int read = 0;
        for (int i = 0; i < handler->ItemsCount; i++)
        {
            uint entryItemId = handler->Items[i].ItemId;
            int entryPrice = handler->Items[i].PriceBuy;
            if (entryItemId == 0) continue;
            read++;
            var known = data.AllItems.FirstOrDefault(x => x.ItemId == entryItemId);
            if (known != null)
            {
                known.RuntimePrice = entryPrice;
                continue;
            }
            if (!itemSheet.TryGetRow(entryItemId, out var item)) continue;
            var slot = ToSlot(item.EquipSlotCategory.ValueNullable);
            var jobs = CosmicJobs(item.ClassJobCategory.ValueNullable);
            if (slot == GearSlot.Unknown || jobs.Count == 0) continue;
            runtime ??= AddRuntimeShop(data);
            runtime.Items.Add(new ShopGearItem
            {
                ItemId = entryItemId,
                Name = item.Name.ExtractText(),
                LevelEquip = item.LevelEquip,
                ItemLevel = item.LevelItem.RowId,
                Slot = slot,
                Kind = KindOf(item),
                Price = item.PriceMid,
                RuntimePrice = entryPrice,
                Jobs = jobs,
                Bracket = BracketOf(item.LevelEquip),
                ShopName = "(実測)",
            });
        }
        UpdateOwnership(data);
        LastMessage = $"開いているショップから {read} 品目を読み取りました";
        IceLogging.Info(LastMessage, "[LevelingGearShop]");
        return read;
    }

    private static ShopInfo AddRuntimeShop(NpcShopData data)
    {
        var shop = new ShopInfo { ShopId = 0, Name = "(実測)", Path = "ShopEventHandler" };
        data.Shops.Add(shop);
        return shop;
    }

    /// <summary>取得結果を設定フォルダに JSON で書き出す。戻り値: 書き出したパス。</summary>
    public static string ExportJson(NpcShopData data)
    {
        var dir = Svc.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"LevelingGearShop_{data.TerritoryId}_{data.NpcId}.json");
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(path, json);
        LastMessage = $"JSON を書き出しました: {path}";
        IceLogging.Info(LastMessage, "[LevelingGearShop]");
        return path;
    }
}
