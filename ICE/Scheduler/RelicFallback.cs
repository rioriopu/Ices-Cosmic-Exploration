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
        /// その種類を与えるものがあり、かつ掲示板に出ているランク以下で、受注レベルを満たし、要求する機能(WKSFunction)が解放済み。
        /// true のとき、最も条件の緩いミッション(通常ミッションのうち機能が解放済みのもの)のランクと必要レベルを返す。
        /// ある種類を与えるミッションがすべて機能未解放なら、その ID を functionOnly に入れ、types をその種類だけにする
        /// (レベルやランクでは解決しないので、呼び出し側は解放条件を案内して止める)。
        /// </summary>
        public static bool IsBlocked(uint job, int jobLv, uint highestRank, IEnumerable<int> neededTypes, IEnumerable<uint> selectable,
            out uint reqRank, out uint reqLevel, out string types, out string detail, out List<uint> functionOnly)
        {
            reqRank = 0; reqLevel = 0; types = ""; detail = "";
            functionOnly = new List<uint>();
            var territory = Player.Territory.RowId;
            bool anyObtainable = false;
            uint minRank = uint.MaxValue, minLevel = uint.MaxValue;
            var blocked = new List<string>();
            var fnOnlyNames = new List<string>();
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

                // 要求する機能が未解放のミッションは、掲示板に並んでもロック表示が無いまま受注が拒否される(実機 2026-09-28/29)。
                // 掲示板に出ていない間は lockedBasic に入らず selectable に残るため、ここで除く
                // (除かないと「受注可」と数え、受けられないミッションを待ってリロールし続ける)
                var reachable = givers.Where(m => CosmicHandler.IsMissionFunctionUnlocked(m.MissionId)).ToList();
                int fnLockedCount = givers.Count - reachable.Count;

                var obtainable = reachable.Where(m => selectableSet.Contains(m.MissionId) && m.Rank <= highestRank && m.Level <= jobLv).ToList();
                if (obtainable.Count > 0)
                {
                    anyObtainable = true;
                    lines.Add($"{typeName}: 受注可 {string.Join(",", obtainable.Take(5).Select(m => $"{m.MissionId}({RankName(m.Rank)}/Lv{m.Level})"))}");
                    continue;
                }
                if (reachable.Count == 0)
                {
                    // レベルを上げてもランクを開けても受けられない。解放条件(前提クエスト)の案内が要る
                    functionOnly.AddRange(givers.Select(m => m.MissionId));
                    fnOnlyNames.Add(typeName);
                    blocked.Add(typeName);
                    lines.Add($"{typeName}: 受注不可(候補{givers.Count}件すべて要求機能が未解放 機能[{string.Join(",", givers.Select(m => m.FunctionId).Distinct())}])");
                    continue;
                }
                // 必要ランク/レベルは機能が解放済みのミッションから決める(機能未解放のものはランクを開けても受けられない)
                var easiest = reachable.OrderBy(m => m.Rank).ThenBy(m => m.Level).First();
                minRank = Math.Min(minRank, easiest.Rank);
                minLevel = Math.Min(minLevel, easiest.Level);
                blocked.Add(typeName);
                lines.Add($"{typeName}: 受注不可(最低 {RankName(easiest.Rank)}クラス/Lv{easiest.Level}、候補{givers.Count}件、うちライブラリ内{givers.Count(m => selectableSet.Contains(m.MissionId))}件{(fnLockedCount > 0 ? $"、機能未解放{fnLockedCount}件" : "")})");
            }

            detail = string.Join(" | ", lines);
            if (anyObtainable || blocked.Count == 0)
            {
                functionOnly.Clear(); // 他の種類が受けられるうちは止めない(その種類が埋まった後の判定で止まる)
                return false;
            }
            reqRank = minRank == uint.MaxValue ? 0 : minRank;
            reqLevel = minLevel == uint.MaxValue ? 0 : minLevel;
            // 機能未解放で止めるときは、その種類だけを文に出す(ランク/レベルで受けられないだけの種類は含めない)
            types = functionOnly.Count > 0 ? string.Join("/", fnOnlyNames) : string.Join("/", blocked);
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

        /// <summary>
        /// 「Lv90 はブロンズで通過」の一時レベリングを開始する。ランクの条件は付けず、targetLevel(通常 91)に到達したら戻る。
        /// Lv90 の製作は Lv90 用の表(推奨作業精度 2805)で難易度が跳ね、Lv91 から新しい装備が着けられるため、
        /// Lv90 ではコスモデータ(金賞)を狙わず、レベリングのミッションをブロンズで回して Lv91 へ上げる。
        /// Lv91 到達後は装備更新(NeedsEquipForLevel)が走ってから B クラスの製作に入る。
        /// </summary>
        public static bool BeginLevelPass(uint job, uint targetLevel)
        {
            string key = $"{job}:0:{targetLevel}:Lv90pass";
            if (_lastEndKey == key && (DateTime.Now - _lastEndAt).TotalSeconds < 60)
            {
                IceLogging.ChatError(IsJapanese
                    ? $"レリックモード: Lv90 通過のレベリングとの切替が短時間に繰り返されています。条件判定が噛み合っていないため停止します。ログを確認してください"
                    : $"Relic mode: switching back and forth with the Lv90 pass-through leveling repeatedly. Stopping; check the log", "[I.C.E.]");
                return false;
            }
            Active = true;
            Job = job;
            RequiredRank = 0;
            RequiredLevel = targetLevel;
            NeededTypes = "Lv90pass";
            IceLogging.ChatInfo(IsJapanese
                ? $"レリックモード: Lv90 はコスモデータを狙わず、レベリング（ブロンズ）で Lv{targetLevel} まで上げます。Lv{targetLevel} で最強装備を行ってから B クラスのミッションに戻ります"
                : $"Relic mode: at Lv90 ICE levels with bronze results until Lv{targetLevel} instead of farming relic data, then equips the best gear and returns to rank B missions", "[I.C.E.]");
            return true;
        }

        /// <summary>レベリングを終えて戻るときの理由文(ApplyAtStart 用)。ランク条件なしの Lv90 通過ではレベルだけを書く。</summary>
        private static string ReturnReason()
        {
            if (RequiredRank == 0)
                return IsJapanese ? $"Lv{RequiredLevel}に到達" : $"reached Lv{RequiredLevel}";
            // A クラス以上はレベルだけで戻る(ShouldStay 参照)。ランクの解放はレリックモード側で進める
            uint rank = ObservedHighestRank.TryGetValue(Job, out var r) ? r : 0;
            if (RequiredRank >= 4 && rank < RequiredRank)
                return IsJapanese
                    ? $"Lv{RequiredLevel}に到達。{RankName(RequiredRank)}クラスの解放はレリックモードで Bクラスの金賞/達成を進めて行います"
                    : $"reached Lv{RequiredLevel}; rank {RankName(RequiredRank)} will be unlocked in Relic mode by completing/golding rank B missions";
            return IsJapanese
                ? $"Lv{RequiredLevel}に到達し{RankName(RequiredRank)}クラスが解放された"
                : $"reached Lv{RequiredLevel} and rank {RankName(RequiredRank)} is unlocked";
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
            // A クラス以上の解放はレベリングモードに処理が無い(レベリングの解放処理は C/B のみで、Lv100 では経験値も入らない)。
            // 必要レベルに達したらレリックモードへ戻し、レリック側の解放処理(Bクラスの金賞/達成)で進める
            if (RequiredRank >= 4 && lv >= RequiredLevel)
                return false;
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
            End(ReturnReason());
            return ModeSelect.RelicMode;
        }
    }
}
