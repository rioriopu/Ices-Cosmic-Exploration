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
        /// 「受けられる」= レリックモードの候補(selectable: ミッションライブラリに残った通常ミッション。緊急/暫定は除く)に
        /// その種類を与えるものがあり、かつ掲示板に出ているランク以下で、受注レベルを満たしている。
        /// true のとき、最も条件の緩いミッション(通常ミッションのみ)のランクと必要レベルを返す。
        /// </summary>
        public static bool IsBlocked(uint job, int jobLv, uint highestRank, IEnumerable<int> neededTypes, IEnumerable<uint> selectable,
            out uint reqRank, out uint reqLevel, out string types, out string detail)
        {
            reqRank = 0; reqLevel = 0; types = ""; detail = "";
            var territory = Player.Territory.RowId;
            bool anyObtainable = false;
            uint minRank = uint.MaxValue, minLevel = uint.MaxValue;
            var blocked = new List<string>();
            var lines = new List<string>();
            var selectableSet = new HashSet<uint>(selectable ?? Array.Empty<uint>());

            foreach (var type in neededTypes)
            {
                string typeName = CosmicHelper.ExpDictionary.TryGetValue(type, out var n) ? n : type.ToString();
                // 緊急(赤警報)は散発的でレリックの候補選択にも乗らないため、得られる手段として数えない。
                // マスター(Rank 6)や「Only Enabled」で無効のミッションは条件を満たしてもライブラリに入らないので、
                // 母集団から外す(入れると「条件は満たすのに候補に無い」状態で往復ループになる)
                var givers = CosmicHelper.SheetMissionDict.Values
                    .Where(m => m.TerritoryId == territory && m.Jobs.Contains(job) && !m.IsProvisional && !m.IsCritical && !m.IsMaster
                                && (!C.XPRelicOnlyEnabled || (C.MissionConfig.TryGetValue(m.MissionId, out var cfg) && cfg.Enabled))
                                && m.RelicXpInfo.TryGetValue(type, out var xp) && xp > 0)
                    .ToList();
                if (givers.Count == 0)
                {
                    lines.Add($"{typeName}: この惑星の通常ミッション(有効なもの)では得られない");
                    continue; // 判定対象外(レベリングでは解決しない)
                }

                var obtainable = givers.Where(m => selectableSet.Contains(m.MissionId) && m.Rank <= highestRank && m.Level <= jobLv).ToList();
                if (obtainable.Count > 0)
                {
                    anyObtainable = true;
                    lines.Add($"{typeName}: 受注可 {string.Join(",", obtainable.Take(5).Select(m => $"{m.MissionId}({RankName(m.Rank)}/Lv{m.Level})"))}");
                    continue;
                }
                var easiest = givers.OrderBy(m => m.Rank).ThenBy(m => m.Level).First();
                minRank = Math.Min(minRank, easiest.Rank);
                minLevel = Math.Min(minLevel, easiest.Level);
                blocked.Add(typeName);
                lines.Add($"{typeName}: 受注不可(最低 {RankName(easiest.Rank)}クラス/Lv{easiest.Level}、候補{givers.Count}件、うちライブラリ内{givers.Count(m => selectableSet.Contains(m.MissionId))}件)");
            }

            detail = string.Join(" | ", lines);
            if (anyObtainable || blocked.Count == 0)
                return false;
            reqRank = minRank;
            reqLevel = minLevel;
            types = string.Join("/", blocked);
            return true;
        }

        // 直前の復帰(End)の記録。同じ理由で短時間に再び切り替わる=往復ループの検知に使う
        private static DateTime _lastEndAt = DateTime.MinValue;
        private static string _lastEndKey = "";

        /// <summary>一時レベリングを開始する。直前に同じ理由で復帰したばかり(60秒以内)なら往復ループとみなし false を返す。</summary>
        public static bool Begin(uint job, uint reqRank, uint reqLevel, string types)
        {
            string key = $"{job}:{reqRank}:{reqLevel}:{types}";
            if (_lastEndKey == key && (DateTime.Now - _lastEndAt).TotalSeconds < 60)
            {
                IceLogging.ChatError(IsJapanese
                    ? $"レリックモード: レベリングとの切替が短時間に繰り返されています（{types}/{RankName(reqRank)}クラス）。条件判定が噛み合っていないため停止します。ログを確認してください"
                    : $"Relic mode: switching back and forth with Leveling repeatedly ({types}/rank {RankName(reqRank)}). Stopping; check the log", "[I.C.E.]");
                return false;
            }
            Active = true;
            Job = job;
            RequiredRank = reqRank;
            RequiredLevel = reqLevel;
            NeededTypes = types;
            IceLogging.ChatInfo(IsJapanese
                ? $"レリックモード: コスモデータ{types}は{RankName(reqRank)}クラスのミッションでしか得られず、{RankName(reqRank)}クラスは未解放です（Lv{reqLevel}以上と下位クラスの達成が必要）。条件を満たすまで一時的にレベリングモードで動きます"
                : $"Relic mode: analysis {types} only comes from rank {RankName(reqRank)} missions, which are not unlocked yet (needs Lv{reqLevel} and the lower rank completed). Switching to Leveling mode until then", "[I.C.E.]");
            return true;
        }

        public static void End(string reason)
        {
            if (!Active) return;
            _lastEndAt = DateTime.Now;
            _lastEndKey = $"{Job}:{RequiredRank}:{RequiredLevel}:{NeededTypes}";
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
