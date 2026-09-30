using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_Craft
    {
        public static void Enqueue()
        {
            // ミッション境界では製作対象が無いので何もしない(以降のミッション情報参照を保護する)。
            if (CosmicHelper.CurrentLunarMission == 0)
                return;

            if (P.Artisan.IsBusy())
            {
                P.TaskManager.Enqueue(() => WaitingForArtisan(), "Waiting for artisan to finish crafting");
                P.TaskManager.Enqueue(() => Task_CheckScore.Enqueue(), "Checking score");
            }
            else
            {
                // ミッション中にレベルが上がっていたら、次の製作の前に最強装備へ更新する(製作スタンスを抜けてから)。
                // コスモレシピは製作者のレベルに応じて難易度が上がるため、装備が追いつかないと Artisan が手順を組めない。
                bool equipPending = C.LevelingGear_AutoEquipBest && Task_RelicTurnin.NeedsEquipForLevel((uint)Player.Job);

                // 装備が壊れている(耐久 0%)と、Artisan は製作開始時に「You have broken gear. Artisan will not continue.」を出して
                // 製作画面を開いたまま何もしなくなる。指示する前に確認し、停滞監視(180 秒×やり直し)を待たずにミッションを終える
                // (2026-09-30 02:55〜07:51 実機: 耐久 0% のまま「停滞→やり直し」を 95 回繰り返した)。
                // 最強装備への更新を予定しているときは、新しい装備で直る可能性があるので先に着替えさせる(着替えても壊れていれば次で拾う)
                if (!equipPending && PlayerHelper.HasBrokenEquippedGear())
                {
                    HandleBrokenGear("[Task Craft]", "製作前の耐久確認");
                    return;
                }

                if (equipPending)
                {
                    IceLogging.Info($"レベルが上がっているため、製作の前に最強装備を行います(Lv{Player.GetLevel(Player.Job)})", "[Task Craft]");
                    P.TaskManager.Enqueue(() => ExitCraftingStance(), "Exiting crafting stance before equipping", CleanupTaskConfig);
                    Task_RelicTurnin.EnqueueEquipBestGear();
                }
                P.TaskManager.Enqueue(() => Task_CheckScore.Enqueue(), "Checking Score");
                P.TaskManager.Enqueue(() => CheckMaterials(), "Checking materials", Utils.TaskConfig);
            }
        }

        // ---- Artisan のソルバー(Raphael)が手順を組めなかった場合 ----
        // Artisan はチャットに「Raphael ... NoSolution」「Raphael has timed out or cancelled ...」を出したあと、
        // 製作画面を開いたまま無効状態で止まる(IsBusy=true のまま)。180 秒の監視を待たずに即座に対処するため、
        // ICE.cs のチャット監視から通知を受ける。
        private static DateTime _raphaelFailedAt = DateTime.MinValue;
        private static string _raphaelFailureText = "";
        private static DateTime _craftIssuedAt = DateTime.MinValue;   // 最後に CraftItem を呼んだ時刻
        private static uint _lastRecipeId = 0;
        private static uint _equipRetryMission = 0;                    // このミッションで「装備更新→再試行」を行ったか

        public static void NotifyRaphaelFailure(string text)
        {
            _raphaelFailedAt = DateTime.Now;
            _raphaelFailureText = text;
        }

        /// <summary>
        /// Artisan が「Raphael CLI not found」を出した(raphael-cli.bin が読めない。Artisan 更新直後やウイルス対策の隔離)。
        /// Raphael は使えないので、以降は標準ソルバーへ置き換えて製作を続け、止まっている製作はやり直す。
        /// </summary>
        public static void NotifyRaphaelUnavailable(string text)
        {
            if (!P.Artisan.RaphaelUnavailable)
            {
                P.Artisan.RaphaelUnavailable = true;
                P.Artisan.ClearSettingsCache();
                IceLogging.Warning($"Artisan の Raphael CLI が見つかりません(「{text}」)。この Start の間は標準ソルバーへ置き換えて製作します。Artisan を再インストール(または raphael-cli.bin をウイルス対策の除外に)してください", "[Task Craft]");
                IceLogging.ChatError(Loc.T("Artisan's Raphael CLI is missing, so ICE will craft with the Standard solver until you restart ICE. Reinstall Artisan or whitelist raphael-cli.bin."), "[I.C.E.]");
            }
            _raphaelFailedAt = DateTime.Now;
            _raphaelFailureText = text;
        }

        /// <summary>
        /// Artisan が「You haven't unlocked Manipulation」を出した(Raphael はマニピュレーション未習得のジョブでは手順を組めない)。
        /// 能力値の問題ではなくジョブのクラスクエスト未達なので、そのジョブでは標準ソルバーへ置き換える。
        /// </summary>
        public static void NotifyRaphaelNeedsManipulation(string text)
        {
            _raphaelFailedAt = DateTime.Now;
            _raphaelFailureText = text;
        }

        // ---- 装備の破損(耐久 0%)----
        // Artisan は製作開始時に装備の最低耐久が 0% だと「You have broken gear. Artisan will not continue.」を出し(日本語クライアントでも英文)、
        // 製作画面を開いたまま何もしなくなる(IsBusy=true のまま)。待っても直らないので、停滞監視を待たずに対処する。
        private static DateTime _brokenGearAt = DateTime.MinValue;   // Artisan の通知を受けた時刻(MinValue=未通知)
        private static string _brokenGearText = "";
        private static int _brokenGearHits = 0;                       // 修理されないまま装備破損で製作を止めた回数(製作が進むか Start で 0)
        private const int BrokenGearMaxHits = 2;                      // この回数に達したら ICE を止める(ミッション終了→修理の判定を挟んでも直っていない)

        /// <summary>
        /// Artisan が「You have broken gear. Artisan will not continue.」を出した(ICE.cs のチャット監視から呼ぶ)。
        /// 次の WaitingForArtisan で実際の耐久と照らし合わせ、壊れていれば 180 秒の停滞監視を待たずに製作を中止してミッションを終える。
        /// </summary>
        public static void NotifyBrokenGear(string text)
        {
            _brokenGearAt = DateTime.Now;
            _brokenGearText = text;
        }

        /// <summary>
        /// 装備の破損で製作できないときの対処。製作を中止して後始末をしてから、1 回目はミッションを終えて(スコアがあれば報告、
        /// なければ放棄。Task_AbandonMission が判定)Start → HubActivityCheck の修理へ戻す。能力値の問題ではないので MarkInfeasible はしない。
        /// 修理されないまま BrokenGearMaxHits 回に達したら、ミッションは受注したまま ICE を止める(修理後に Start で再開できる)。
        /// </summary>
        internal static bool? HandleBrokenGear(string tag, string source)
        {
            var mission = CosmicHelper.CurrentLunarMission;
            string text = string.IsNullOrEmpty(_brokenGearText) ? "-" : _brokenGearText;
            _brokenGearAt = DateTime.MinValue;
            _brokenGearText = "";
            _raphaelFailedAt = DateTime.MinValue;
            _brokenGearHits++;
            ResetArtisanWatch();
            _craftActionLockSince = DateTime.MinValue;
            _artisanStallRecoveries = 0;
            _artisanStallTotal = 0;
            StopArtisan();

            IceLogging.Warning($"装備が壊れているため製作できません({source}: 「{text}」 装備の最低耐久 {PlayerHelper.GetMinEquippedConditionPercent()}%、{_brokenGearHits}/{BrokenGearMaxHits} 回目)。停滞監視を待たずに製作を中止します {PlayerHelper.DescribeBrokenGear()}", tag);

            // 製作画面・スタンスを閉じてから次へ(放棄も手動の修理も、製作の状態が残っていると拒否される)
            P.TaskManager.Tasks.Clear();
            P.TaskManager.EnqueueMulti(
                new(() => CancelSynthesisIfOpen(), "Cancelling synthesis (broken gear)", CleanupTaskConfig),
                new(() => ExitCraftingStance(), "Exiting crafting stance (broken gear)", CleanupTaskConfig),
                new(() => WaitArtisanSettled(), "Waiting for Artisan to settle", CleanupTaskConfig));

            if (_brokenGearHits >= BrokenGearMaxHits)
            {
                // ミッション終了→ハブでの修理判定を挟んでも直っていない。放置しても直らないので止める。
                // DisablePlugin は TaskManager を Abort して上の後始末まで消すため使わない(Stop_DarkMatter と同じ止め方)
                IceLogging.Error($"装備の破損が修理されないまま {_brokenGearHits} 回続いたため、ICE を停止します(ミッション {mission} は受注したまま)", tag);
                IceLogging.ChatError(Loc.T("Your gear is still broken, so ICE stopped. Repair your gear (at a mender or with Dark Matter) and press Start again."), "[I.C.E.]");
                _brokenGearHits = 0;
                SchedulerMain.State = IceState.Idle;
                return true;
            }

            IceLogging.ChatError(Loc.T("Your gear is broken (0% durability), so ICE stopped crafting and will end this mission, then repair your gear."), "[I.C.E.]");
            SchedulerMain.State = IceState.AbandonMission;
            return true;
        }

        /// <summary>
        /// 装備が使える状態に戻ったことを確認したとき(修理の成功、デュアルミッションの製作完了)に呼ぶ。
        /// 戻さないと、修理を挟んだ後の次の破損が「修理されないまま 2 回続いた」と数えられ、修理を試さずに止まる
        /// </summary>
        internal static void ResetBrokenGearHits() => _brokenGearHits = 0;

        /// <summary>
        /// Task_DualClass 用: Artisan から装備破損の通知があり、実際に装備が壊れているか。
        /// 壊れていなければ(他のプレイヤーの発言・修理前の古い通知)通知を捨てる。
        /// </summary>
        internal static bool IsBrokenGearBlockingCraft()
        {
            if (_brokenGearAt == DateTime.MinValue)
                return false;
            if (PlayerHelper.GetMinEquippedConditionPercent() > 0)
            {
                _brokenGearAt = DateTime.MinValue;
                _brokenGearText = "";
                return false;
            }
            return true;
        }

        /// <summary>
        /// ミッションを新しく受注したとき(Task_ExecuteMission から呼ぶ)。同じミッション ID を続けて受けても、前回の停滞回数を持ち越さない
        /// (ICE は同じミッションを何時間も繰り返し受けることが多く、ID だけで区別すると一時的な停滞が積み上がって正常なミッションを放棄してしまう)
        /// </summary>
        internal static void OnMissionStarted(uint missionId)
        {
            _artisanStallMission = missionId;
            _artisanStallRecoveries = 0;
            _artisanStallTotal = 0;
        }

        /// <summary>
        /// Raphael が解を出せなかった製作への対処。1 回目は最強装備へ更新して再試行(ミッション中のレベルアップで
        /// 装備が追いついていない典型例)、2 回目は「現在の能力値では作れない」と判断してミッションを放棄し、
        /// レベルか装備(作業精度)が変わるまでそのミッションを候補から外す。
        /// </summary>
        private static bool? HandleUnsolvableCraft(string tag)
        {
            var mission = CosmicHelper.CurrentLunarMission;
            uint job = (uint)Player.Job;
            string diag = DescribeCraftFeasibility(_lastRecipeId);
            IceLogging.Warning($"Artisan(Raphael)がこの製作の手順を組めませんでした: 「{_raphaelFailureText}」 {diag}", tag);
            bool manipulationMissing = _raphaelFailureText.Contains("Manipulation", StringComparison.OrdinalIgnoreCase);
            _raphaelFailedAt = DateTime.MinValue;
            ResetArtisanWatch();
            _craftActionLockSince = DateTime.MinValue;
            StopArtisan();

            bool retryWithGear = C.LevelingGear_AutoEquipBest && _equipRetryMission != mission;
            P.TaskManager.Tasks.Clear();
            P.TaskManager.EnqueueMulti(
                new(() => CancelSynthesisIfOpen(), "Cancelling unsolvable synthesis", CleanupTaskConfig),
                new(() => ExitCraftingStance(), "Exiting crafting stance", CleanupTaskConfig),
                new(() => WaitArtisanSettled(), "Waiting for Artisan to settle", CleanupTaskConfig));
            if (P.Artisan.RaphaelUnavailable)
            {
                // Raphael 自体が使えない(CLI 不在)。能力値の問題ではないので、標準ソルバーに置き換えてそのままやり直す
                IceLogging.Info("Raphael CLI が無いため、標準ソルバーで製作をやり直します", tag);
                return true;
            }
            if (manipulationMissing)
            {
                // マニピュレーション未習得(Lv65 クラスクエスト未達)。Raphael はこのジョブでは何度やっても解を出せないので、
                // このジョブに限って標準ソルバーへ置き換える(Start で解除)。能力値が明らかに足りない場合はそのまま下の判定へ
                if (P.Artisan.RaphaelBlockedJobs.Add(job))
                {
                    P.Artisan.ClearSettingsCache();
                    IceLogging.Warning($"ジョブ {(Job)job} はマニピュレーション未習得のため Raphael が使えません。この Start の間はこのジョブを標準ソルバーで製作します", tag);
                    IceLogging.ChatError(Loc.T("Raphael cannot be used on this job because Manipulation is not unlocked. Complete the job's class quests (Lv65). ICE will craft with the Standard solver on this job."), "[I.C.E.]");
                }
                if (!IsClearlyUnderGeared(_lastRecipeId, out var csNow, out var suggested))
                {
                    IceLogging.Info($"能力値は足りている(作業精度 {csNow} / 推奨 {suggested})ので、標準ソルバーで製作をやり直します", tag);
                    return true;
                }
                IceLogging.Warning($"作業精度 {csNow} が推奨 {suggested} に大きく届いていないため、標準ソルバーでも完成できません。装備の判定へ進みます", tag);
            }
            if (retryWithGear)
            {
                _equipRetryMission = mission;
                IceLogging.Info("最強装備へ更新してから製作をやり直します", tag);
                IceLogging.ChatInfo(Loc.T("Artisan could not build a rotation for this craft. Updating gear and retrying once."), "[I.C.E.]");
                Task_RelicTurnin.EnqueueEquipBestGear();
                // キューが空になると Tick が Craft 状態を組み直し、スコア確認→材料確認→再指示(新しい能力値で Raphael が再計算)となる
                return true;
            }

            AbandonAsInfeasible(mission, job, tag);
            return true;
        }

        // 現在の能力値では完成できない製作として、ミッションを放棄し候補から外す
        private static void AbandonAsInfeasible(uint mission, uint job, string tag)
        {
            int craftsmanship = GetCraftsmanship();
            Task_CheckMissions.MarkInfeasible(mission, job, Player.GetLevel((Job)job), craftsmanship);
            IceLogging.Warning($"ミッション {mission} は現在の能力値(Lv{Player.GetLevel((Job)job)} 作業精度 {craftsmanship})では完成できないと判断し、放棄して候補から外します(レベルか装備が変わるまで)", tag);
            IceLogging.ChatError(Loc.T("This craft cannot be completed with the current stats. The mission was abandoned and will be skipped until your level or gear changes."), "[I.C.E.]");
            _artisanStallRecoveries = 0;
            _artisanStallTotal = 0;
            SchedulerMain.State = IceState.AbandonMission;
        }

        // ログ用: 製作者の能力値・レベル、Artisan がコスモレシピに当てはめるレベル表、レシピの実効値、ステディハンドの残り回数
        private static unsafe string DescribeCraftFeasibility(uint recipeId)
        {
            try
            {
                int lv = Player.GetLevel(Player.Job);
                int cs = GetCraftsmanship();
                var ps = UIState.Instance()->PlayerState;
                int ctl = ps.Attributes[71];
                int cp = ps.Attributes[11];
                string recipeInfo = "";
                if (ExcelHelper.RecipeSheet.TryGetRow(recipeId, out var recipe))
                {
                    var own = recipe.RecipeLevelTable.Value;
                    // Artisan はコスモレシピ(recipe.Number == 0)で製作者レベル < 100 なら、そのレベルに対応する最初の RecipeLevelTable を使う
                    var lt = lv < 100 && recipe.Number == 0
                        ? Svc.Data.GetExcelSheet<RecipeLevelTable>().First(x => x.ClassJobLevel == lv)
                        : own;
                    int progress = lt.Difficulty * recipe.DifficultyFactor / 100;
                    int quality = (int)(lt.Quality * recipe.QualityFactor / 100);
                    int durability = own.Durability * recipe.DurabilityFactor / 100;
                    recipeInfo = $"レシピ {recipeId}(rlvl {own.RowId}→Lv{lv} 用の表 {lt.RowId}: 推奨作業精度 {lt.SuggestedCraftsmanship}) 必要工数 {progress} 品質上限 {quality} 耐久 {durability}";
                }
                string steady = "不明";
                var dam = DutyActionManager.GetInstanceIfReady();
                if (dam != null)
                    steady = $"{dam->ActionId[0]}:{dam->CurCharges[0]} / {dam->ActionId[1]}:{dam->CurCharges[1]}";
                return $"[Lv{lv} 作業精度 {cs} 加工精度 {ctl} CP {cp} | {recipeInfo} | 特殊アクション(ID:残回数) {steady}]";
            }
            catch (Exception ex)
            {
                return $"[診断情報の取得に失敗: {ex.Message}]";
            }
        }

        private static unsafe int GetCraftsmanship()
        {
            try { return UIState.Instance()->PlayerState.Attributes[70]; } catch { return 0; }
        }

        // 作業精度が、Artisan が当てはめるレベル表の推奨値に大きく届いていないか(6 割未満)。
        // 実機(2026-09-26): Lv90 で作業精度 867(推奨 2805)の裁縫師は、どのソルバーでも B ランクの製作を完成できなかった。
        private static bool IsClearlyUnderGeared(uint recipeId, out int craftsmanship, out int suggested)
        {
            craftsmanship = GetCraftsmanship();
            suggested = 0;
            try
            {
                int lv = Player.GetLevel(Player.Job);
                if (ExcelHelper.RecipeSheet.TryGetRow(recipeId, out var recipe))
                {
                    var lt = lv < 100 && recipe.Number == 0
                        ? Svc.Data.GetExcelSheet<RecipeLevelTable>().First(x => x.ClassJobLevel == lv)
                        : recipe.RecipeLevelTable.Value;
                    suggested = lt.SuggestedCraftsmanship;
                }
            }
            catch { }
            return suggested > 0 && craftsmanship < suggested * 0.6;
        }

        /// <summary>
        /// 製作に関わる状態(スタンス・製作画面・レシピ帳)が残っているか。
        /// この状態ではミッションの放棄/報告がゲームに拒否される(「現在の状態では実行できません」)。
        /// </summary>
        internal static bool IsCraftingStateActive()
            => Svc.Condition[ConditionFlag.Crafting]
               || Svc.Condition[ConditionFlag.PreparingToCraft]
               || Svc.Condition[ConditionFlag.ExecutingCraftingAction]
               || AddonHelper.IsAddonActive("Synthesis")
               || AddonHelper.IsAddonActive("WKSRecipeNotebook")
               || AddonHelper.IsAddonActive("RecipeNote");

        /// <summary>
        /// 製作の状態から抜けるための 1 tick 分の後始末。毎 tick 呼ぶ。全部片付いていれば true。
        /// Artisan の Endurance が生きていれば止める: CraftItem(CraftX)は「レシピ選択が終わったら Endurance を有効化する」タスクを
        /// Artisan 側に遅延で積むため、こちらが一度止めた後に再び有効になり、レシピ帳を開き直してスタンスに戻ることがある
        /// (実機 2026-09-26: 放棄が 23 分間・75,786 回拒否され続けた)。
        /// </summary>
        internal static bool TryLeaveCraftingState(string tag)
        {
            if (SafeArtisan(P.Artisan.GetEnduranceStatus) && EzThrottler.Throttle("Craft leave: endurance off", 1000))
            {
                IceLogging.Info("Artisan の Endurance が有効なままなので止めます", tag);
                StopArtisan();
            }
            if (CancelSynthesisIfOpen() != true)
                return false;
            if (ExitCraftingStance() != true)
                return false;
            return !IsCraftingStateActive();
        }

        // 単一の製作アクションがロックし始めた時刻(アニメーションロック検知用)。MinValue=未計測。
        private static DateTime _craftActionLockSince = DateTime.MinValue;

        // ---- Artisan 停止監視 ----
        // Artisan が「動作中(IsBusy)」を返し続けているのに、製作アクション(ExecutingCraftingAction)が一定時間まったく出ない
        // 状態を「停止」とみなす。製作が進んでいれば数秒おきに必ずアクションが出るので、Artisan 内部の状態や
        // 製作スタンスの有無に関係なく検知できる。
        // 1.0.0.19 の監視は「製作スタンス(Crafting/PreparingToCraft)に入っていない時間」を数えていたため、
        // レシピ帳を開いたまま(スタンスに入ったまま)Artisan が製作を始められないケースを検知できなかった
        // (実機: 前工程 x2 の直後に主製作へレシピを切り替えるところで、ログが「Telling Artisan to craft」で途切れて停止)。
        private static DateTime _artisanWaitSince = DateTime.MinValue;    // 今回の待機の開始時刻(MinValue=待機していない)
        private static DateTime _lastCraftActionAt = DateTime.MinValue;   // 最後に製作アクション(開始のアニメーションを含む)を観測した時刻(待機開始時に初期化)
        private static DateTime _lastWaitPollAt = DateTime.MinValue;      // 最後に WaitingForArtisan が呼ばれた時刻(待機タスクが外部で打ち切られたかの判定用)
        private static int _artisanStallRecoveries = 0;                   // 同一ミッション内の連続の復旧回数(実際に製作が進んだら 0 に戻す。上限を超えたら放棄)
        private static int _artisanStallTotal = 0;                        // 同一ミッション内の通算の復旧回数(製作が進んでも戻さない。無限ループ防止)
        private static uint _artisanStallMission = 0;                     // 復旧回数を数えているミッション
        private const double ArtisanStallSeconds = 60;                    // 製作画面が開いていないときの許容秒数(レシピ選択・食事・開始は通常 10 秒以内)
        private const double ArtisanStallInCraftSeconds = 180;            // 製作画面(Synthesis)が開いているときの許容秒数(ソルバーの計算待ちを考慮)
        private const int ArtisanStallMaxRecoveries = 2;
        private const int ArtisanStallMaxPerMission = 5;                  // 1 ミッションで許す通算のやり直し回数(6 回目の停滞で放棄)
        private const double WaitSessionGapSeconds = 5;                   // これ以上呼ばれていなければ、前回の待機(指示)タスクは外部で打ち切られたとみなす

        // ---- 「実際に製作が進んだか」の判定用 ----
        // 製作開始のアニメーションでも ExecutingCraftingAction は立つため、それとは別に「工程が進んだ」「完成品が増えた」を見る
        private static int _synthStepSeen = -1;           // この待機で最後に見た製作画面の工程(-1=まだ見ていない)
        private static uint _craftTargetItemId = 0;       // 最後に Artisan へ指示した製作物(完成数で進行を判定する)
        private static int _craftItemCountBaseline = -1;  // この待機での製作物の所持数の基準(-1=未取得)
        // 復旧時の後始末(製作の中止・スタンス解除)は、失敗しても次へ進めるよう時間制限付き・打ち切り無しで実行する
        private static readonly ECommons.Automation.NeoTaskManager.TaskManagerConfiguration CleanupTaskConfig = new(timeLimitMS: 20000, abortOnTimeout: false);

        private static bool? WaitingForArtisan()
        {
            string tag = "Craft: Waiting for Artisan";
            var now = DateTime.Now;

            // 前回の呼び出しから間が空いた = 前の待機タスクは外部(Stop / 別タスクの Tasks.Clear / TaskManager の Abort・時間切れ)で
            // 打ち切られている。その時刻を引き継ぐと、次の待機の最初の判定で即座に停滞扱いになる
            // (2026-09-30 11:14: 上限 60 秒のところ最初の判定が 189 秒。9/29〜30 には 832 秒・12874 秒・44940 秒も出ていた)
            if (_artisanWaitSince != DateTime.MinValue && (now - _lastWaitPollAt).TotalSeconds > WaitSessionGapSeconds)
            {
                IceLogging.Info($"前回の Artisan 待機は {(now - _lastWaitPollAt).TotalSeconds:F0} 秒前に打ち切られていたため、停滞監視の時刻を数え直します(残っていた待機 {(now - _artisanWaitSince).TotalSeconds:F0} 秒)", tag);
                ResetArtisanWatch();
                _craftActionLockSince = DateTime.MinValue;
            }
            _lastWaitPollAt = now;

            // Artisan が装備の破損を理由に製作を拒否した → 待っても始まらないので即座に中止する。
            // チャットは他のプレイヤーの発言や修理前の古い通知の可能性もあるので、実際の耐久と照らし合わせる(読めなければ通知を信用する)
            if (_brokenGearAt != DateTime.MinValue && _brokenGearAt >= _craftIssuedAt)
            {
                int pct = PlayerHelper.GetMinEquippedConditionPercent();
                if (pct > 0)
                {
                    IceLogging.Info($"「{_brokenGearText}」を受けましたが、装備の最低耐久は {pct}% のため無視します", tag);
                    _brokenGearAt = DateTime.MinValue;
                    _brokenGearText = "";
                }
                else
                    return HandleBrokenGear(tag, "Artisan の通知");
            }

            if (!P.Artisan.IsBusy())
            {
                // 1 アクションで完成するレシピなどで、待機中に進行を観測できないまま終わることがある。最後に完成数だけ確かめる
                if (_craftTargetItemId != 0 && _craftItemCountBaseline >= 0
                    && PlayerHelper.GetItemCount(_craftTargetItemId, out var endCount) && endCount > _craftItemCountBaseline)
                {
                    _brokenGearHits = 0;
                    if (_artisanStallMission == CosmicHelper.CurrentLunarMission)
                        _artisanStallRecoveries = 0;
                }
                _craftActionLockSince = DateTime.MinValue;
                ResetArtisanWatch();
                IceLogging.Info("Artisan is no longer running, continuing the process", tag);
                return true;
            }
            else
            {
                if (_artisanWaitSince == DateTime.MinValue)
                {
                    _artisanWaitSince = now;
                    _lastCraftActionAt = now;
                }

                // Artisan(Raphael)が「解なし」を出した → 待っても始まらないので即座に対処する
                if (_raphaelFailedAt != DateTime.MinValue && _raphaelFailedAt >= _craftIssuedAt)
                    return HandleUnsolvableCraft(tag);

                bool executing = Svc.Condition[ConditionFlag.ExecutingCraftingAction];
                bool synthOpen = AddonHelper.IsAddonActive("Synthesis");
                // 停止判定の時計は、製作アクション(開始のアニメーションを含む)が出ていれば進めない
                if (executing)
                    _lastCraftActionAt = now;

                // 復旧回数を 0 に戻すのは「実際に製作が進んだ」ときだけ。製作開始のアニメーションでも ExecutingCraftingAction は立つため、
                // それを進行とみなすと、開始直後に Artisan が止まり続ける状況(装備破損など)で毎回 (1/2) に戻り、放棄に届かない
                // (2026-09-30 02:55〜07:51: 装備耐久 0% で Artisan が製作を拒否し、(1/2) のやり直しを 95 回繰り返した)
                if (DetectRealCraftProgress(synthOpen, out var progressed))
                {
                    _lastCraftActionAt = now;
                    _brokenGearHits = 0; // 製作が進んだ = 装備は使える状態
                    if (_artisanStallMission == CosmicHelper.CurrentLunarMission && _artisanStallRecoveries > 0)
                    {
                        IceLogging.Info($"製作が進んだため({progressed})、停止からの連続復旧回数を戻します({_artisanStallRecoveries}→0、このミッションの通算 {_artisanStallTotal} 回は維持)", tag);
                        _artisanStallRecoveries = 0;
                    }
                }

                double idle = (now - _lastCraftActionAt).TotalSeconds;
                double limit = synthOpen ? ArtisanStallInCraftSeconds : ArtisanStallSeconds;
                if (idle >= 20 && EzThrottler.Throttle("Artisan stall log", 20000))
                    IceLogging.Debug($"Artisan 待機中: 最後の製作アクションから {idle:F0} 秒(上限 {limit:F0} 秒) {ArtisanStateSummary(synthOpen)}", tag);

                if (idle >= limit)
                    return HandleArtisanStall(idle, synthOpen, tag);

                // 単一の製作アクションが異常に長くロックしている状態からの脱出。通常1アクションは数秒なので、
                // 20秒以上続く場合はハングとみなしてミッションを放棄し、棒立ちのまま止まるのを防ぐ。
                if (executing)
                {
                    if (_craftActionLockSince == DateTime.MinValue)
                        _craftActionLockSince = now;
                    else if ((now - _craftActionLockSince).TotalSeconds >= 20)
                    {
                        IceLogging.Warning("製作アクションが20秒以上ロックしています(アニメーションロックの可能性)。ミッションを放棄して復帰します", tag);
                        _craftActionLockSince = DateTime.MinValue;
                        ResetArtisanWatch();
                        SchedulerMain.State = IceState.AbandonMission;
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }
                }
                else
                {
                    _craftActionLockSince = DateTime.MinValue; // アクションの合間はリセットする
                }

                if (GenericHelpers.TryGetAddonMaster<WKSHud>(out var moonHud))
                {
                    if (!AddonHelper.IsAddonActive("WKSMissionInfomation"))
                    {
                        if (EzThrottler.Throttle("Opening mission scoring info"))
                        {
                            moonHud.Mission();
                        }
                    }
                }
            }
            return false;
        }

        private static void ResetArtisanWatch()
        {
            _artisanWaitSince = DateTime.MinValue;
            _lastCraftActionAt = DateTime.MinValue;
            // 「製作が進んだか」の基準も取り直す(次の待機で最初に見た値を基準にする)
            _synthStepSeen = -1;
            _craftItemCountBaseline = -1;
        }

        /// <summary>
        /// 製作の監視状態をすべて初期化する(SchedulerMain.EnablePlugin / DisablePlugin から呼ぶ)。
        /// Stop や外部の Abort で待機タスクが途中で消えると、待機開始時刻・最後のアクション時刻・Artisan からの通知が残り、
        /// 次の待機の最初の判定で即座に停滞扱いになっていた(2026-09-30 11:14: 上限 60 秒のところ最初の判定が 189 秒)。
        /// </summary>
        internal static void ResetCraftWatch()
        {
            ResetArtisanWatch();
            _lastWaitPollAt = DateTime.MinValue;
            _lastIssuePollAt = DateTime.MinValue;
            _craftActionLockSince = DateTime.MinValue;
            _artisanStallRecoveries = 0;
            _artisanStallTotal = 0;
            _artisanStallMission = 0;
            _raphaelFailedAt = DateTime.MinValue;
            _raphaelFailureText = "";
            _brokenGearAt = DateTime.MinValue;
            _brokenGearText = "";
            _brokenGearHits = 0;
            _craftTargetItemId = 0;
            _stanceExitSince = DateTime.MinValue;
            throttleCounter = 0;
        }

        /// <summary>
        /// 実際に製作が進んだか。製作画面の工程が 2 以上へ進んだ(=この製作で 1 つ以上アクションが実行された)か、
        /// 指示した製作物の所持数が増えた(=完成した)ときだけ true。製作開始のアニメーション(ExecutingCraftingAction が一瞬立つ)や、
        /// 製作画面が開いただけでは true にしない。
        /// </summary>
        private static bool DetectRealCraftProgress(bool synthOpen, out string what)
        {
            what = "";
            bool advanced = false;

            if (synthOpen)
            {
                if (TryReadSynthesis(out int step, out int progress, out int quality))
                {
                    if (_synthStepSeen < 0 || step < _synthStepSeen)
                    {
                        // この待機で初めて見た製作画面、または次の製作が始まった(工程が戻った)。基準を取るだけで進行とはみなさない
                        _synthStepSeen = step;
                    }
                    else if (step > _synthStepSeen)
                    {
                        // 工程 1 は開始直後(まだアクションなし)。2 以上に上がって初めて「アクションが実行された」とみなす
                        if (step >= 2)
                        {
                            advanced = true;
                            what = $"工程 {_synthStepSeen}→{step}(工数 {progress} 品質 {quality})";
                        }
                        _synthStepSeen = step;
                    }
                }
            }
            else
            {
                // 製作画面が閉じた(完成・中止)。次に開いた製作は新しい基準で見る
                _synthStepSeen = -1;
            }

            // 完成数(1 アクションで完成して工程 2 を観測できない場合や、工程が読めない場合の保険)
            if (_craftTargetItemId != 0 && EzThrottler.Throttle("Craft progress: item count", 1000)
                && PlayerHelper.GetItemCount(_craftTargetItemId, out int count))
            {
                if (_craftItemCountBaseline < 0)
                    _craftItemCountBaseline = count;
                else if (count > _craftItemCountBaseline)
                {
                    advanced = true;
                    what += (what.Length > 0 ? " / " : "") + $"完成品 {_craftItemCountBaseline}→{count}";
                    _craftItemCountBaseline = count;
                }
                else if (count < _craftItemCountBaseline)
                {
                    _craftItemCountBaseline = count; // 主製作の材料として使われた等で減った分は、基準を下げるだけ
                }
            }
            return advanced;
        }

        /// <summary>
        /// 製作画面(Synthesis)の工程・工数・品質を読む。Artisan(GameInterop/Crafting.cs)と同じ AtkValues の位置
        /// ([15]=工程 [5]=工数 [9]=品質、AtkValuesCount 26 以上)を使う。
        /// ECommons の AddonMaster.Synthesis.Reader は値の型が UInt 以外だと例外を投げるため、Int / UInt のどちらでも受け付けるよう直接読む。
        /// 読めなければ false。
        /// </summary>
        private static unsafe bool TryReadSynthesis(out int step, out int progress, out int quality)
        {
            step = 0;
            progress = 0;
            quality = 0;
            try
            {
                if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Synthesis", out var addon) || !GenericHelpers.IsAddonReady(addon))
                    return false;
                if (addon->AtkValues == null || addon->AtkValuesCount < 26)
                    return false;
                var v = addon->AtkValues;
                if (!IsNumber(v[15].Type) || !IsNumber(v[5].Type) || !IsNumber(v[9].Type))
                    return false;
                step = v[15].Int;
                progress = v[5].Int;
                quality = v[9].Int;
                return true;
            }
            catch
            {
                return false;
            }

            static bool IsNumber(AtkValueType t) => t is AtkValueType.Int or AtkValueType.UInt;
        }

        // 停止を検知したときの復旧。上限回数までは Artisan を止めて後始末をしてから通常の流れ(スコア確認→材料確認→再指示)に戻し、
        // 超えたらミッションを放棄する。どちらも製作画面・レシピ帳を閉じてからでないと次の操作(再指示・放棄)ができない。
        // 上限は 2 つ: 連続の復旧回数(実際に製作が進むと 0 に戻る)と、ミッション内の通算回数(戻らない)。
        private static bool? HandleArtisanStall(double idle, bool synthOpen, string tag)
        {
            // 装備が壊れているなら、やり直しても Artisan は製作を拒否し続ける(チャットの通知を取りこぼしたときの保険)
            if (PlayerHelper.HasBrokenEquippedGear())
                return HandleBrokenGear(tag, $"{idle:F0} 秒の停滞");

            var mission = CosmicHelper.CurrentLunarMission;
            if (_artisanStallMission != mission)
            {
                _artisanStallMission = mission;
                _artisanStallRecoveries = 0;
                _artisanStallTotal = 0;
            }
            _artisanStallRecoveries++;
            _artisanStallTotal++;
            string state = ArtisanStateSummary(synthOpen);
            ResetArtisanWatch();
            _craftActionLockSince = DateTime.MinValue;

            if (_artisanStallRecoveries <= ArtisanStallMaxRecoveries && _artisanStallTotal <= ArtisanStallMaxPerMission)
            {
                IceLogging.Warning($"Artisan が {idle:F0} 秒間製作アクションを出していません。Artisan を停止して製作をやり直します({_artisanStallRecoveries}/{ArtisanStallMaxRecoveries}、このミッションで通算 {_artisanStallTotal}/{ArtisanStallMaxPerMission}) {state}", tag);
                IceLogging.ChatInfo(Loc.T(synthOpen
                    ? "Artisan stopped making progress in the middle of a craft, so ICE cancelled it and will retry."
                    : "Artisan did not start crafting, so ICE stopped it and will retry the craft."), "[I.C.E.]");
                StopArtisan();
                // 後始末のあと待機タスクを終える。キューが空になると Tick が Craft 状態を再度組み立て、
                // スコア確認→材料確認→再指示(スタンス解除済みなので Artisan は最初からレシピ帳を開き直す)と流れる
                P.TaskManager.InsertMulti(
                    new(() => CancelSynthesisIfOpen(), "Cancelling stalled synthesis", CleanupTaskConfig),
                    new(() => ExitCraftingStance(), "Exiting crafting stance", CleanupTaskConfig),
                    new(() => WaitArtisanSettled(), "Waiting for Artisan to settle", CleanupTaskConfig));
                return true;
            }

            IceLogging.Warning($"Artisan の停止・再指示を続けても製作が進まないため(連続 {_artisanStallRecoveries - 1} 回・このミッションで通算 {_artisanStallTotal - 1} 回やり直し済み)、ミッションを放棄して復帰します {state} {DescribeCraftFeasibility(_lastRecipeId)}", tag);
            IceLogging.ChatError(Loc.T("Artisan kept failing to start crafting, so the mission was abandoned."), "[I.C.E.]");
            StopArtisan();
            P.TaskManager.Tasks.Clear();
            // 製作画面やレシピ帳が開いたままだと放棄できないので、先に閉じてから Tick に放棄させる
            P.TaskManager.EnqueueMulti(
                new(() => CancelSynthesisIfOpen(), "Cancelling stalled synthesis", CleanupTaskConfig),
                new(() => ExitCraftingStance(), "Exiting crafting stance", CleanupTaskConfig));
            // 同じ能力値で同じミッションを取り直しても同じ結果になるので、レベルか装備が変わるまで候補から外す
            AbandonAsInfeasible(mission, (uint)Player.Job, tag);
            return true;
        }

        // ログ用: Artisan と製作関連の状態を一行にまとめる
        private static string ArtisanStateSummary(bool synthOpen)
        {
            string synth = synthOpen && TryReadSynthesis(out var step, out var progress, out var quality)
                ? $"工程={step}, 工数={progress}, 品質={quality}"
                : "工程=-";
            return $"[busy={SafeArtisan(P.Artisan.IsBusy)}, endurance={SafeArtisan(P.Artisan.GetEnduranceStatus)}, list={SafeArtisan(P.Artisan.IsListRunning)}, stopReq={SafeArtisan(P.Artisan.GetStopRequest)}, "
                 + $"Crafting={Svc.Condition[ConditionFlag.Crafting]}, Preparing={Svc.Condition[ConditionFlag.PreparingToCraft]}, Executing={Svc.Condition[ConditionFlag.ExecutingCraftingAction]}, "
                 + $"製作画面={synthOpen}, {synth}, レシピ帳={AddonHelper.IsAddonActive("WKSRecipeNotebook")}, 選択中={SelectedNotebookItem() ?? "-"}, 装備の最低耐久={PlayerHelper.GetMinEquippedConditionPercent()}%]";
        }

        // コスモレシピ帳で現在選択されているアイテム名(取得できなければ null)
        private static string SelectedNotebookItem()
        {
            try
            {
                if (GenericHelpers.TryGetAddonMaster<WKSRecipeNotebook>("WKSRecipeNotebook", out var notebook) && notebook.IsAddonReady)
                    return notebook.SelectedCraftingItem;
            }
            catch { }
            return null;
        }

        // Artisan の IPC 問い合わせ(Func<bool>)を安全に評価する(Artisan 未ロード時は false)
        private static bool SafeArtisan(Func<bool> f)
        {
            try { return f != null && f(); } catch { return false; }
        }

        // 固まった Artisan を止める。
        // 1) Endurance(CraftX の繰り返し)を無効化: Artisan 側で前処理タスク(レシピ選択など)も一緒に破棄される
        // 2) 停止要求→解除: 停止要求で Artisan は残った前処理を捨ててレシピ帳を閉じる(スタンス解除)。
        //    Endurance を先に止めているので、解除しても Artisan が勝手に再開することはない
        private static void StopArtisan()
        {
            try
            {
                P.Artisan.SetEnduranceStatus(false);
                P.Artisan.SetStopRequest(true);
                P.Artisan.SetStopRequest(false);
            }
            catch (Exception ex)
            {
                IceLogging.Debug($"Artisan の停止に失敗: {ex.Message}", "Craft: Waiting for Artisan");
            }
        }

        // 製作画面(Synthesis)が開いたままなら中止する(中止確認の SelectYesno は「はい」)。閉じていれば即完了。
        private static bool? CancelSynthesisIfOpen()
        {
            string tag = "Craft: Cancel synthesis";
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Craft stall cancel yesno", 500))
                    yesno.Yes();
                return false;
            }
            if (!AddonHelper.IsAddonActive("Synthesis"))
                return true;
            if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
                return false; // アクション中は操作できない
            if (EzThrottler.Throttle("Craft stall cancel synthesis", 1000))
            {
                IceLogging.Warning("製作が進まないため製作画面を中止します", tag);
                GenericHandlers.FireCallback("Synthesis", true, -1);
            }
            return false;
        }

        // 製作スタンス(Crafting/PreparingToCraft)を抜ける。レシピ帳を閉じれば抜けられる。抜けていれば即完了。
        // レシピ帳が見えているのにスタンスの条件が立っていない一瞬もあるので、レシピ帳が見えている間も閉じ続ける。
        private static bool? ExitCraftingStance()
        {
            bool notebookOpen = AddonHelper.IsAddonActive("WKSRecipeNotebook") || AddonHelper.IsAddonActive("RecipeNote");
            if (!Svc.Condition[ConditionFlag.Crafting] && !Svc.Condition[ConditionFlag.PreparingToCraft] && !notebookOpen)
                return true;
            if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
                return false;
            if (AddonHelper.IsAddonActive("WKSRecipeNotebook"))
            {
                if (EzThrottler.Throttle("Craft exit stance", 1000))
                    GenericHandlers.FireCallback("WKSRecipeNotebook", true, -1);
            }
            else if (AddonHelper.IsAddonActive("RecipeNote"))
            {
                if (EzThrottler.Throttle("Craft exit stance", 1000))
                    GenericHandlers.FireCallback("RecipeNote", true, -1);
            }
            else if (EzThrottler.Throttle("Craft exit stance: waiting", 5000))
            {
                // レシピ帳は閉じたがスタンスの条件が残っている。通常は数秒で解除される
                IceLogging.Debug($"製作スタンスの解除を待っています(Crafting={Svc.Condition[ConditionFlag.Crafting]}, Preparing={Svc.Condition[ConditionFlag.PreparingToCraft]})", "Craft: Exit stance");
            }
            return false;
        }

        // Artisan が停止処理を終えて IsBusy=false になるのを待つ(時間制限は CleanupTaskConfig)。
        // 途中で Endurance が再び有効になっていたら止め直す(CraftX の遅延タスクが後から有効化する)
        private static bool? WaitArtisanSettled()
        {
            if (SafeArtisan(P.Artisan.GetEnduranceStatus))
            {
                if (EzThrottler.Throttle("Craft settle: endurance off", 1000))
                {
                    IceLogging.Info("Artisan の Endurance が再び有効になっていたため止めます", "Craft: Waiting for Artisan");
                    StopArtisan();
                }
                return false;
            }
            return !P.Artisan.IsBusy();
        }

        // レシピ切り替え前にスタンスを抜け始めた時刻(MinValue=未実施)
        private static DateTime _stanceExitSince = DateTime.MinValue;
        private const double StanceExitMaxSeconds = 15;

        // 製作スタンスに入ったまま(レシピ帳が開いたまま)別のレシピを Artisan に指示するべきかどうか。
        // 実機で前工程→主製作の切り替え時に Artisan がレシピ帳内での選択・開始に失敗して固まったため、
        // レシピが変わるときは一度スタンスを抜け、Artisan に最初(レシピ帳を開くところ)からやらせる。
        // 同じレシピの続き(スコア用の追加製作など)はそのままで問題ないので抜けない。
        private static bool NeedsFreshCraftStance(uint itemId)
        {
            if (!Svc.Condition[ConditionFlag.PreparingToCraft] && !Svc.Condition[ConditionFlag.Crafting])
                return false;
            if (!AddonHelper.IsAddonActive("WKSRecipeNotebook"))
                return false;
            var selected = SelectedNotebookItem();
            if (string.IsNullOrEmpty(selected))
                return true; // 何が選ばれているか分からないので安全側(開き直す)
            string targetName = "";
            try
            {
                if (Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
                    targetName = item.Name.ToString();
            }
            catch { }
            if (string.IsNullOrEmpty(targetName))
                return true;
            return !string.Equals(selected.Trim(), targetName.Trim(), StringComparison.Ordinal);
        }

        private static uint throttleCounter = 0;
        private static DateTime _lastIssuePollAt = DateTime.MinValue; // 最後に ThrottleArtisanTaskV2 が呼ばれた時刻(指示タスクが外部で打ち切られたかの判定用)
        private static void InsertArtisanWait(KeyValuePair<ushort, CosmicHelper.CraftingInfo> item, int amount)
        {
            P.TaskManager.InsertMulti(
                new(() => ThrottleArtisanTaskV2(item, amount), "Telling artisan to craft"),
                new(() => WaitingForArtisan(), "Waiting for artisan")
            );
        }

        private static bool? ThrottleArtisanTaskV2(KeyValuePair<ushort, CosmicHelper.CraftingInfo> item, int amount)
        {
            // 前回の指示タスクが Stop / 外部の Tasks.Clear で打ち切られていると、待ち回数とスタンス解除の開始時刻が残り、
            // 「15 秒以内にスタンスを抜けられなかった」と誤判定してスタンスを抜けずに指示してしまう。間が空いていたら数え直す
            var pollNow = DateTime.Now;
            if ((pollNow - _lastIssuePollAt).TotalSeconds > WaitSessionGapSeconds)
            {
                throttleCounter = 0;
                _stanceExitSince = DateTime.MinValue;
            }
            _lastIssuePollAt = pollNow;

            int delay = C.DelayCraft ? C.DelayCraftIncrease : 25;

            var craftId = item.Key;
            var recipeId = item.Value.RecipeId;
            var itemId = item.Value.ItemId;
            var expert = item.Value.ExpertCraft;

            var missionId = CosmicHelper.CurrentLunarMission;
            // ミッション境界(missionId==0)や未設定ミッションでは MissionConfig 未登録で例外になるため早期return。
            if (missionId == 0 || !C.MissionConfig.TryGetValue(missionId, out var missionConfig))
                return true;

            if (missionConfig.CraftSettings.TryGetValue(recipeId, out var recipeConfig))
            {
                if (EzThrottler.Throttle("Applying Config States", 1000))
                {
                    IceLogging.Info($"Applying config states for the following recipeID: {recipeId}");
                    // レベリング中に Progress Only へ切り替えるのは設定で選んだときだけ(既定は ICE で設定したソルバーをそのまま使う)
                    bool levelingProgressOnly = Mission_Settings.Mode == ModeSelect.LevelMode && C.Leveling_UseProgressOnlySolver;
                    P.Artisan.CheckArtisanSettings((ushort)recipeId, CosmicHelper.CurrentLunarMission, expert, levelingProgressOnly);
                }
            }
            else
            {
                missionConfig.CraftSettings[recipeId] = new();
                C.Save();
            }

            if (EzThrottler.Throttle("Waiting X Amount of seconds for artisan", delay))
            {
                throttleCounter += 1;
            }

            if (throttleCounter >= 3)
            {
                // レシピを切り替えるときは製作スタンスを一度抜けてから指示する(上限秒数を過ぎたらそのまま続行)
                if (!P.Artisan.IsBusy() && NeedsFreshCraftStance(itemId))
                {
                    if (_stanceExitSince == DateTime.MinValue)
                    {
                        _stanceExitSince = DateTime.Now;
                        IceLogging.Info($"レシピ帳の選択({SelectedNotebookItem() ?? "-"})と製作対象({itemId})が異なるため、製作スタンスを一度抜けてから Artisan に指示します", "[Task Craft]");
                    }
                    if ((DateTime.Now - _stanceExitSince).TotalSeconds < StanceExitMaxSeconds)
                    {
                        ExitCraftingStance();
                        return false;
                    }
                    if (EzThrottler.Throttle("Stance exit give up", 5000))
                        IceLogging.Warning($"{StanceExitMaxSeconds:F0} 秒以内に製作スタンスを抜けられなかったため、そのまま Artisan に指示します", "[Task Craft]");
                }

                if (EzThrottler.Throttle("Artisan Crafting Task"))
                {
                    IceLogging.Debug($"Telling Artisan to craft: {itemId} -> {amount} times");
                    _lastRecipeId = recipeId;
                    _craftIssuedAt = DateTime.Now;
                    _raphaelFailedAt = DateTime.MinValue;
                    _brokenGearAt = DateTime.MinValue;
                    // 新しい指示ごとに停滞監視と進行判定の基準を取り直す(前の待機の時刻を持ち越さない)。
                    // 完成数で「製作が進んだ」を判定するため、指示した製作物を覚えておく
                    _craftTargetItemId = itemId;
                    ResetArtisanWatch();
                    _craftActionLockSince = DateTime.MinValue;
                    P.Artisan.CraftItem(craftId, amount);
                }

                if (P.TaskManager.IsBusy)
                {
                    throttleCounter = 0;
                    _stanceExitSince = DateTime.MinValue;
                    return true;
                }
            }

            return false;
        }

        private static bool? CheckMaterials()
        {
            var id = CosmicHelper.CurrentLunarMission;
            var mission = CosmicHelper.SheetMissionDict[id];

            bool provisional = mission.IsProvisional;

            if (!P.Artisan.IsBusy())
            {
                if (mission.Crafts_Pre.Count > 0)
                {
                    // Mission has pre-crafts that are required. 
                    // Checking to see if you have enough pre-crafts first
                    var preCraft = mission.Crafts_Pre.FirstOrDefault();
                    var mainCraft = mission.Crafts_Main.FirstOrDefault();

                    var preItemId = preCraft.Value.ItemId;
                    var mainItemId = mainCraft.Value.ItemId;

                    PlayerHelper.GetItemCount(preCraft.Value.ItemId, out var preItemAmount);
                    PlayerHelper.GetItemCount(preCraft.Value.RequiredItems.FirstOrDefault().Key, out var moonCrateCount);
                    PlayerHelper.GetItemCount(mainCraft.Value.ItemId, out var mainItemCount);

                    if (preItemAmount >= mainCraft.Value.RequiredItems[preItemId])
                    {
                        IceLogging.Info($"Required pre-Item count: {mainCraft.Value.RequiredItems[preItemId]} | amount necessary: {preItemAmount}");

                        // There's enough items to craft the mainhand. Telling it to craft it instead. 
                        if (mainItemCount < mainCraft.Value.RequiredAmount)
                        {
                            // you don't have enough of the pre-crafts to craft the main item. 
                            // going to tell artisan to just kick it into gear
                            var craftAmount = mainCraft.Value.RequiredAmount - mainItemCount;
                            InsertArtisanWait(mainCraft, craftAmount);
                            IceLogging.Info($"Telling artisan to craft: {mainCraft.Value.ItemId} -> {craftAmount}", "[Task Craft: Check Materials]");
                            return true;
                        }
                        else
                        {
                            // you have enough of the main hand item. But you still are crafting. So time to just craft 1 more
                            InsertArtisanWait(mainCraft, 1);
                            IceLogging.Info($"Current item count of: {mainCraft.Value.ItemId} | {mainItemCount}");
                            IceLogging.Info($"Telling artisan to craft: {mainCraft.Value.ItemId} -> 1", "[Task Craft: Check Materials]");
                            return true;
                        }

                    }
                    else if (moonCrateCount >= preCraft.Value.RequiredAmount)
                    {
                        // You should have enough to make this pre-craft. Initiating the thing now.
                        var craftAmount = preCraft.Value.RequiredAmount - preItemAmount;

                        if (mainCraft.Value.RequiredAmount > 1 && mainItemCount == 0)
                        {
                            craftAmount = mainCraft.Value.RequiredAmount * (preCraft.Value.RequiredAmount - preItemAmount);
                        }
                        if (craftAmount < 1)
                            craftAmount = 1;

                        bool SpecialExpert = preCraft.Value.ExpertCraft && provisional;
                        InsertArtisanWait(preCraft, craftAmount);
                        IceLogging.Info($"Found a material that still needed to be crafted", "[Task Craft: Check Materials]");
                        return true;
                    }
                    else
                    {
                        IceLogging.Info($"Somehow, out of mats. Need to exit. And either attempt to turnin, or just straight up abandon.", "[Task Craft: Check Materials]");
                        SchedulerMain.State = IceState.AbandonMission;
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }
                }
                else
                {
                    // This is the case when you need multiple items, or even just a single item.
                    foreach (var craft in mission.Crafts_Main)
                    {
                        PlayerHelper.GetItemCount(craft.Value.ItemId, out var reqAmount);
                        if (reqAmount < craft.Value.RequiredAmount)
                        {
                            // If you need less than what is necessary, this should change the count to be proper
                            reqAmount = craft.Value.RequiredAmount - reqAmount;

                            // Found an item that needs to be crafted. Time to check if you have enough of the material
                            var craftMaterial = ExcelHelper.RecipeSheet.GetRow(craft.Key).Ingredient[0].RowId;
                            if (PlayerHelper.GetItemCount(craftMaterial, out var itemAmount) && itemAmount >= reqAmount)
                            {
                                bool SpecialExpert = craft.Value.ExpertCraft && provisional;
                                InsertArtisanWait(craft, reqAmount);
                                IceLogging.Info($"Telling artisan to craft: {craft.Value.ItemId} -> {reqAmount}", "[Craft: No Pre-Mats]");
                                return true;
                            }
                            else
                            {
                                // You don't have enough to craft this for the mission. Exiting out and checking for score/force abandon
                                IceLogging.Info("You have no remaining items to craft the main crafting items. Going to abandon the mission now", "[Crafts: No Pre-Mats]");
                                SchedulerMain.State = IceState.AbandonMission;
                                P.TaskManager.Tasks.Clear();
                                return true;
                            }
                        }
                    }

                    var moreCraft = mission.Crafts_Main.FirstOrDefault();
                    // If you've gotten this far, that means you still need scoring. Just going to queue up the first mission (if possible)
                    // If you need less than what is necessary, this should change the count to be proper
                    var AdditionalItem = 1;

                    // Found an item that needs to be crafted. Time to check if you have enough of the material
                    var moreCraftMaterial = ExcelHelper.RecipeSheet.GetRow(moreCraft.Key).Ingredient[0].RowId;
                    if (PlayerHelper.GetItemCount(moreCraftMaterial, out var moreItemAmount) && moreItemAmount >= AdditionalItem)
                    {
                        InsertArtisanWait(moreCraft, AdditionalItem);
                        IceLogging.Info($"Telling artisan to craft: {moreCraft.Value.ItemId} -> {AdditionalItem}", "[Craft: No Pre-Mats]");
                        return true;
                    }
                    else
                    {
                        // You don't have enough to craft this for the mission. Exiting out and checking for score/force abandon
                        SchedulerMain.State = IceState.AbandonMission;
                        IceLogging.Info("You have no remaining items to craft the pre-crafts. Going to abandon the mission now", "[Crafts: No Pre-Mats]");
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }

                }
            }
            else
            {
                if (EzThrottler.Throttle("Artisan Busy Log", 3000))
                {
                    IceLogging.Debug("Artisan is currently busy... so we're properly waiting for it to finish");
                }
            }

            return false;
        }
    }
}
