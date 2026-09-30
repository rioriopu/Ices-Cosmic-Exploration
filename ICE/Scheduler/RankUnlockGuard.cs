using ECommons.GameHelpers;
using ICE.Sounds;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;

namespace ICE.Scheduler
{
    /// <summary>
    /// 次のミッションランク(C/B/A)を解放する手段が無い状態の検出と、その解放条件の案内。
    /// ランクの解放には下位ランクのミッションの達成(A は B の金賞も)が要るが、残りの下位ランクのミッションが
    /// 要求する機能(WKSFunction)の未解放で受注できないと、周回を続けてもランクは開かない。
    /// 実機 2026-09-29〜30(錬金術師 / アウクセシア): Lv90 台で B 未解放、残りの C(1544/1545)が機能 3
    /// (クエスト「職人の新たなお仕事」= 収集品)未解放。レリックの一時レベリングが受けられる C(1542/1543)を約 24 時間周回し続けた。
    /// </summary>
    internal static class RankUnlockGuard
    {
        private static bool IsJapanese => Svc.ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.Japanese;

        /// <summary>ジョブのレベル上限(ここに達すると経験値が入らない)</summary>
        public const int MaxLevel = 100;

        /// <summary>純粋なレベリングでの定期案内(10 分に 1 回)の EzThrottler 名。Start で解除する</summary>
        public const string NoticeThrottle = "Rank unlock blocked notice";

        /// <summary>ランクの受注レベル(ICEDictornaryCreation と同じ: D=10, C=50, B=90, A 以上=100)</summary>
        public static int RankLevel(uint rank) => rank switch
        {
            0 => 0,
            1 => 10,
            2 => 50,
            3 => 90,
            _ => 100,
        };

        // 前提クエストの補足(クエスト名だけでは何が解放されるか分かりにくいもの)。
        // 67631「職人の新たなお仕事」= 収集品(機能 3/4/11)、67633「生命、精選、もうひとつの答え」= 精選(機能 4)
        private static readonly Dictionary<uint, (string Ja, string En)> QuestNotes = new()
        {
            [67631] = ("収集品", "collectables"),
            [67633] = ("精選", "aetherial reduction"),
        };

        public sealed class Block
        {
            /// <summary>解放できないランク</summary>
            public uint TargetRank { get; init; }
            /// <summary>解放に必要な下位ランク</summary>
            public uint LowerRank { get; init; }
            /// <summary>未達成のまま受注できない下位ランクのミッション</summary>
            public List<uint> Missions { get; init; } = new();
            /// <summary>理由(例: 「Bクラスの解放に必要なCクラスの残りのミッション(1544,1545)は、要求する機能が未解放で受注できません。」)</summary>
            public string Reason { get; init; } = "";
            /// <summary>解放方法(例: 「解放するには、クエスト『職人の新たなお仕事』(収集品/イシュガルド/Lv50)を完了してください。」)</summary>
            public string Action { get; init; } = "";

            public string Detail => IsJapanese ? $"{Reason}{Action}" : $"{Reason} {Action}";

            public string LogText =>
                $"{RelicFallback.RankName(TargetRank)}クラス解放不可: 下位{RelicFallback.RankName(LowerRank)}クラスの残り[{string.Join(",", Missions)}]の要求機能" +
                $"[{string.Join(",", Missions.Select(id => CosmicHelper.SheetMissionDict.TryGetValue(id, out var m) ? m.FunctionId : 0u).Distinct())}]が未解放 / {Action}";
        }

        /// <summary>
        /// highestRank(受けられる最高ランク)の次から targetRank までの各ランクについて、解放に必要な下位ランクの残りのミッション
        /// (解放用リストに入る通常ミッションのうち未達成のもの。A は B の金賞未満)が、すべて要求する機能の未解放で受注できないか。
        /// 次の場合は判定しない(誤って止めないため):
        /// ・レベルがそのランクの受注レベルに届いていない(レベルで閉じているだけで、下位ランクの条件は満たしている可能性がある)
        /// ・そのランクがゲーム上は開いている(openRanks = 掲示板でロック表示が無いランク。ICE 側の除外で候補から消えているだけ)
        /// ・そのランクのミッションを一度でも達成している(解放済み)
        /// ・残りが 1 つも無い(別の条件で閉じている。ここでは分からない)
        /// </summary>
        public static bool TryDetect(uint job, uint territory, int level, uint highestRank, uint targetRank, ICollection<uint> openRanks, out Block block)
        {
            block = null;
            for (uint rank = Math.Max(highestRank + 1, 2u); rank <= Math.Min(targetRank, 4u); rank++)
            {
                if (level < RankLevel(rank))
                    break;
                if (openRanks != null && openRanks.Contains(rank))
                    continue;

                bool everDone = CosmicHelper.SheetMissionDict.Values.Any(m => m.TerritoryId == territory && m.Jobs.Contains(job) && m.Rank == rank
                                                                          && !m.IsProvisional && !m.IsCritical && m.CompletionStatus != CosmicHelper.Status.None);
                if (everDone)
                    continue;

                uint lower = rank - 1;
                // A(4) の解放は B の金賞も条件に入る(レリックモードの A 解放処理と同じ考え方)ので、金賞未満を「残り」とする
                var remaining = CosmicHelper.SheetMissionDict.Values
                    .Where(m => m.TerritoryId == territory && m.Jobs.Contains(job) && m.Rank == lower
                                && !m.IsProvisional && !m.IsCritical && !m.IsMaster
                                && CosmicMissionLists.IsUnlockMission(m.MissionId)
                                && (rank >= 4 ? m.CompletionStatus < CosmicHelper.Status.Gold : m.CompletionStatus == CosmicHelper.Status.None))
                    .ToList();
                if (remaining.Count == 0)
                    continue;
                // 受けられる残りが 1 つでもあれば、周回(リロール)で解放を進められる
                if (remaining.Any(m => CosmicHandler.IsMissionFunctionUnlocked(m.MissionId)))
                    continue;

                var ids = remaining.Select(m => m.MissionId).OrderBy(x => x).ToList();
                block = new Block
                {
                    TargetRank = rank,
                    LowerRank = lower,
                    Missions = ids,
                    Reason = IsJapanese
                        ? $"{RelicFallback.RankName(rank)}クラスの解放に必要な{RelicFallback.RankName(lower)}クラスの残りのミッション({string.Join(",", ids)})は、要求する機能が未解放で受注できません。"
                        : $"The remaining rank {RelicFallback.RankName(lower)} missions ({string.Join(",", ids)}) needed to unlock rank {RelicFallback.RankName(rank)} require a feature that is not unlocked yet.",
                    Action = DescribeUnlock(ids, territory),
                };
                return true;
            }
            return false;
        }

        /// <summary>レリックモード(一時レベリングを含む): 必要な種類のために要るランクを解放できないので停止するときの文</summary>
        public static string RelicStopMessage(Block b, string types) => IsJapanese
            ? $"レリックモード: コスモデータ{types}に必要な{RelicFallback.RankName(b.TargetRank)}クラスを解放できません。{b.Action}停止します（{b.Reason.TrimEnd('。')}）"
            : $"Relic mode: rank {RelicFallback.RankName(b.TargetRank)}, needed for analysis {types}, cannot be unlocked. {b.Action} Stopping. ({b.Reason.TrimEnd('.')})";

        /// <summary>レリックモード: 必要な種類を与えるミッションがすべて機能未解放で受けられないので停止するときの文</summary>
        public static string RelicFunctionStopMessage(List<uint> missions, string types)
        {
            string ids = string.Join(",", missions.Take(8)) + (missions.Count > 8 ? ",…" : "");
            string action = DescribeUnlock(missions, Player.Territory.RowId);
            return IsJapanese
                ? $"レリックモード: コスモデータ{types}を得られるミッション({ids})は、すべて要求する機能が未解放で受注できません。{action}停止します"
                : $"Relic mode: every mission that gives analysis {types} ({ids}) requires a feature that is not unlocked yet. {action} Stopping.";
        }

        /// <summary>
        /// 理由をチャットに出して自動実行を止める(既存の停止と同じく Idle にしてタスクを空にする)。音の通知が有効なら鳴らす。
        /// ChatError は同じ文を 60 秒間抑止するので、停止してすぐ Start し直して同じ理由で止まったときも出るよう、抑止を解除してから出す
        /// </summary>
        public static void StopWithMessage(string message, string logText, string tag)
        {
            IceLogging.Info($"{logText}。停止します", tag);
            EzThrottler.Reset($"Throttling chat message: {message}");
            IceLogging.ChatError(message, "[I.C.E.]");
            SchedulerMain.State = IceState.Idle;
            P.TaskManager.Tasks.Clear();
            if (C.PlaySoundAlert)
                _ = SoundPlayer.PlaySoundAsync();
        }

        /// <summary>
        /// ミッションが要求する機能(WKSFunction)の解放条件を、未完了の前提クエスト(名前/補足/場所/受注レベル)で案内する文にする。
        /// WKSFunction の各列(RequiredQuests0〜2, RequiredDevGrade)は 4 要素で、要素は惑星ごと
        /// (0=シヌス・アルドラム, 1=パエンナ, 2=オイジュス, 3=アウクセシア = CosmicMoonDefinition.ExpeditionTabIndex)。
        /// 根拠(ゲームデータ): 開発グレードの要件は惑星の要素にだけ入る(機能 1=要素 0、12/13/15/16=要素 1、19/20=要素 2、22/23=要素 3)。
        /// 惑星限定の釣りクエストも、その惑星の要素にだけ入る(機能 10=要素 0 のみ、14=要素 1 のみ)。
        /// 機能 3(収集品)と 4(収集品＋精選)は全要素が同じクエスト。RequiredQuests0〜2 は同じ要素を並べて読む(機能 4 = 67631 と 67633)。
        /// 列どうしが「すべて」か「いずれか」かは未確認のため、未完了のものを全部挙げる。
        /// 未完了のクエストが無ければ開発段階を挙げる(開発段階の達成状況は読めないので「必要です」とだけ書く)。
        /// </summary>
        private static string DescribeUnlock(IEnumerable<uint> missionIds, uint territory)
        {
            var functionIds = missionIds
                .Select(id => CosmicHelper.SheetMissionDict.TryGetValue(id, out var m) ? m.FunctionId : 0u)
                .Where(f => f != 0)
                .Distinct()
                .ToList();
            int idx = CosmicMoonRegistry.TryGetMoon(territory, out var moon) ? moon.ExpeditionTabIndex : -1;
            var quests = new List<string>();
            var grades = new List<string>();
            try
            {
                var fnSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.WKSFunction>();
                var gradeSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.WKSDevGrade>();
                foreach (var fid in functionIds)
                {
                    if (!fnSheet.TryGetRow(fid, out var fn))
                        continue;

                    var questIds = new List<uint>();
                    foreach (var col in new[] { fn.RequiredQuests0, fn.RequiredQuests1, fn.RequiredQuests2 })
                    {
                        if (idx >= 0 && idx < col.Count)
                            questIds.Add(col[idx].RowId);
                        else
                            questIds.AddRange(col.Select(q => q.RowId)); // 惑星が分からないときは全要素
                    }
                    foreach (var qid in questIds.Where(q => q != 0).Distinct())
                    {
                        if (FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(qid))
                            continue;
                        var text = QuestText(qid);
                        if (!quests.Contains(text))
                            quests.Add(text);
                    }

                    var gradeIds = idx >= 0 && idx < fn.RequiredDevGrade.Count
                        ? new List<uint> { fn.RequiredDevGrade[idx].RowId }
                        : fn.RequiredDevGrade.Select(g => g.RowId).ToList();
                    foreach (var gid in gradeIds.Where(g => g != 0).Distinct())
                    {
                        if (!gradeSheet.TryGetRow(gid, out var grade))
                            continue;
                        var stage = grade.Stage.ExtractText().Replace("\n", " ").Trim();
                        if (!string.IsNullOrEmpty(stage) && !grades.Contains(stage))
                            grades.Add(stage);
                    }
                }
            }
            catch (Exception ex)
            {
                IceLogging.Error($"要求する機能の解放条件を読めませんでした(機能 {string.Join(",", functionIds)}): {ex.Message}", "[RankUnlockGuard]");
            }

            if (quests.Count > 0)
                return IsJapanese
                    ? $"解放するには、クエスト{string.Join("、", quests)}を完了してください。"
                    : $"To unlock it, complete {(quests.Count > 1 ? "the quests" : "the quest")} {string.Join(", ", quests)}.";
            if (grades.Count > 0)
                return IsJapanese
                    ? $"解放には、惑星の開発段階 {string.Join("、", grades)} の達成が必要です。"
                    : $"It unlocks when the planet's development reaches {string.Join(", ", grades)}.";
            return IsJapanese
                ? $"解放条件を特定できませんでした(機能 {string.Join(",", functionIds)})。"
                : $"The unlock condition could not be identified (feature {string.Join(",", functionIds)}).";
        }

        /// <summary>クエストの表示名。例: 『職人の新たなお仕事』(収集品/イシュガルド/Lv50)</summary>
        private static string QuestText(uint questId)
        {
            string name = questId.ToString();
            var parts = new List<string>();
            if (QuestNotes.TryGetValue(questId, out var note))
                parts.Add(IsJapanese ? note.Ja : note.En);
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().TryGetRow(questId, out var q))
            {
                var n = q.Name.ExtractText();
                if (!string.IsNullOrEmpty(n))
                    name = n;
                var place = q.PlaceName.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrEmpty(place))
                    parts.Add(place);
                if (q.ClassJobLevel.Count > 0 && q.ClassJobLevel[0] > 0)
                    parts.Add($"Lv{q.ClassJobLevel[0]}");
            }
            string extra = parts.Count == 0 ? "" : IsJapanese ? $"({string.Join("/", parts)})" : $" ({string.Join(" / ", parts)})";
            return IsJapanese ? $"『{name}』{extra}" : $"\"{name}\"{extra}";
        }
    }
}
