using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;

namespace ICE.Scheduler
{
    /// <summary>
    /// レリックモードの一時的なレベリング切替。
    /// 主道具の強化に必要なコスモデータの種類(例: Ⅴ)が、未解放のランク(例: Bクラス)のミッションでしか得られない場合、
    /// そのランクのミッションを受けに行こうとせず、条件(必要レベル到達＋ランク解放)を満たすまでレベリングモードで動き、
    /// 満たしたらレリックモードへ戻す。ランクの解放状況は掲示板を開いた時に見えた最高ランクで判断する。
    /// </summary>
    internal static class RelicFallback
    {
        public static bool Active { get; private set; }
        public static uint Job { get; private set; }
        public static uint RequiredRank { get; private set; }
        public static uint RequiredLevel { get; private set; }
        public static string NeededTypes { get; private set; } = "";

        // ジョブごとに掲示板で最後に見た最高ランク(=解放済みランク)。掲示板を開くたびに更新する。
        public static readonly Dictionary<uint, uint> ObservedHighestRank = new();

        private static bool IsJapanese => Svc.ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.Japanese;

        public static string RankName(uint rank) => rank switch
        {
            1 => "D",
            2 => "C",
            3 => "B",
            4 => "A",
            5 => "EX",
            _ => rank.ToString(),
        };

        public static void Observe(uint job, uint highestRank)
        {
            if (job != 0)
                ObservedHighestRank[job] = highestRank;
        }

        public static void Reset()
        {
            Active = false;
            Job = 0;
            RequiredRank = 0;
            RequiredLevel = 0;
            NeededTypes = "";
        }

        /// <summary>
        /// 必要な種類のコスモデータを得られるミッションが、いまのランク解放状況・レベルでは1つも受けられないか。
        /// true のとき、最も条件の緩いミッションのランクと必要レベルを返す。
        /// </summary>
        public static bool IsBlocked(uint job, int jobLv, uint highestRank, IEnumerable<int> neededTypes,
            out uint reqRank, out uint reqLevel, out string types)
        {
            reqRank = 0; reqLevel = 0; types = "";
            var territory = Player.Territory.RowId;
            bool anyObtainable = false;
            uint minRank = uint.MaxValue, minLevel = uint.MaxValue;
            var blocked = new List<string>();

            foreach (var type in neededTypes)
            {
                var givers = CosmicHelper.SheetMissionDict.Values
                    .Where(m => m.TerritoryId == territory && m.Jobs.Contains(job) && !m.IsProvisional
                                && (!m.IsCritical || C.Relic_IncludeCriticals)
                                && m.RelicXpInfo.TryGetValue(type, out var xp) && xp > 0)
                    .ToList();
                if (givers.Count == 0)
                    continue; // この惑星では得られない種類。判定対象外(別の理由で進まない)

                if (givers.Any(m => m.Rank <= highestRank && m.Level <= jobLv))
                {
                    anyObtainable = true;
                    continue;
                }
                var easiest = givers.OrderBy(m => m.Rank).ThenBy(m => m.Level).First();
                minRank = Math.Min(minRank, easiest.Rank);
                minLevel = Math.Min(minLevel, easiest.Level);
                blocked.Add(CosmicHelper.ExpDictionary.TryGetValue(type, out var n) ? n : type.ToString());
            }

            if (anyObtainable || blocked.Count == 0)
                return false;
            reqRank = minRank;
            reqLevel = minLevel;
            types = string.Join("/", blocked);
            return true;
        }

        public static void Begin(uint job, uint reqRank, uint reqLevel, string types)
        {
            Active = true;
            Job = job;
            RequiredRank = reqRank;
            RequiredLevel = reqLevel;
            NeededTypes = types;
            IceLogging.ChatInfo(IsJapanese
                ? $"レリックモード: コスモデータ{types}は{RankName(reqRank)}クラスのミッションでしか得られず、{RankName(reqRank)}クラスは未解放です（Lv{reqLevel}以上と下位クラスの達成が必要）。条件を満たすまで一時的にレベリングモードで動きます"
                : $"Relic mode: analysis {types} only comes from rank {RankName(reqRank)} missions, which are not unlocked yet (needs Lv{reqLevel} and the lower rank completed). Switching to Leveling mode until then", "[I.C.E.]");
        }

        public static void End(string reason)
        {
            if (!Active) return;
            IceLogging.ChatInfo(IsJapanese
                ? $"レリックモードに戻ります（{reason}）"
                : $"Returning to Relic mode ({reason})", "[I.C.E.]");
            Reset();
        }

        /// <summary>レベリングを続けるべきか(必要レベル未達 or ランク未解放)。</summary>
        public static bool ShouldStay(uint job)
        {
            if (!Active || job != Job) return false;
            int lv = Player.GetLevel((Job)job);
            uint rank = ObservedHighestRank.TryGetValue(job, out var r) ? r : 0;
            return lv < RequiredLevel || rank < RequiredRank;
        }

        /// <summary>
        /// 各サイクルの開始時(モードを設定から読み直す所)で呼ぶ。レリックモード選択中にフォールバック中なら、
        /// 条件を満たすまで LevelMode、満たしたら RelicMode に戻す。
        /// </summary>
        public static ModeSelect ApplyAtStart(ModeSelect selected, uint job)
        {
            if (selected != ModeSelect.RelicMode || !Active)
                return selected;
            if (job != Job)
            {
                End(IsJapanese ? "ジョブが変わった" : "job changed");
                return selected;
            }
            if (ShouldStay(job))
                return ModeSelect.LevelMode;
            End(IsJapanese ? $"Lv{RequiredLevel}に到達し{RankName(RequiredRank)}クラスが解放された" : $"reached Lv{RequiredLevel} and rank {RankName(RequiredRank)} is unlocked");
            return ModeSelect.RelicMode;
        }
    }
}
