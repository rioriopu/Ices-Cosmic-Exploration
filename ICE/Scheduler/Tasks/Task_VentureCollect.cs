using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.IPC;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 拠点の呼び鈴でリテイナーのベンチャーを回収する。回収そのもの(受け取り・再出発)は AutoRetainer に任せ、
    /// ICE は「区切りで拠点へ戻る → 呼び鈴まで歩く → 開く → AutoRetainer が終わるのを待つ → 画面を閉じる」だけを行う。
    /// Task_HubActivities の用事のひとつとして、ミッション未受注のタイミング(HubActivityCheck)で実行される。
    /// 呼び鈴の扱い(見えている≠話しかけられる、読み込まれていない≠存在しない、IsBusy の一瞬の false を信じない)は
    /// GbrVentureRelay の BellRunner で実機確認済みの作りを移植した。
    /// </summary>
    internal static class Task_VentureCollect
    {
        private const string Tag = "[Venture Collect]";

        // 各拠点の呼び鈴の座標(ゲームデータ planlive.lgb から抽出。Sinus は EObj 2014985、他は 2000441)。
        // 呼び鈴が見えていればその位置を優先し、この座標は「まだ読み込まれていない」ときの移動先に使う。
        private static readonly Dictionary<uint, Vector3> BellLocations = new()
        {
            [1237] = new(10.53f, 1.61f, 17.29f),      // Sinus Ardorum(拠点中心から約 19m)
            [1291] = new(358.02f, 52.62f, -409.64f),  // Phaenna(約 18m)
            [1310] = new(-197.29f, 0.50f, 157.68f),   // Oizys(約 33m)
            [1319] = new(312.75f, 205.64f, 371.76f),  // Auxesia(約 22m)
        };

        private const float InteractRange = 3.5f;          // これより遠いと「距離が離れています」になる
        private const float MoveStopDistance = 2.5f;       // 移動の目標(対話範囲より内側)
        private const float ArrivedRange = InteractRange + 1f;
        private const uint BellEObjNameRow = 2000401;      // 呼び鈴の名前を引く EObjName 行(言語に依らない)
        private static readonly string[] RetainerAddons = ["RetainerList", "SelectString", "SelectYesno", "RetainerTaskAsk", "RetainerTaskResult"];

        private static readonly TimeSpan OverallLimit = TimeSpan.FromMinutes(8);   // 1 回の回収全体の上限
        private static readonly TimeSpan SearchGrace = TimeSpan.FromSeconds(45);   // 呼び鈴が読み込まれるのを待つ猶予
        private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(10);    // 回収できるはずなのに AutoRetainer が動き出さないときの猶予
        private static readonly TimeSpan IdleBeforeClose = TimeSpan.FromSeconds(3); // 「忙しくない」がこれだけ続いたら終わったとみなす
        private static readonly TimeSpan VentureCheckInterval = TimeSpan.FromSeconds(5);

        // 失敗時のクールダウン: 3 回未満は 2 分、以上は設定値(既定 30 分)。成功後も短い間隔を空けて連続発火を防ぐ
        private const int FailuresBeforeLongWait = 3;
        private static readonly TimeSpan ShortRetryDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan AfterSuccessDelay = TimeSpan.FromMinutes(5);
        private static int _consecutiveFailures = 0;
        private static DateTime _nextAttemptAllowedAt = DateTime.MinValue;

        // 判定のキャッシュ(AutoRetainer へ毎フレーム聞かない)
        private static DateTime _lastVentureCheck = DateTime.MinValue;
        private static AutoRetainerIPC.VentureState _lastVentureState = AutoRetainerIPC.VentureState.Unknown;

        // 1 回の回収の進行状態
        private static bool _failed;
        private static string _failReason = "";
        private static DateTime _beganAt = DateTime.MinValue;
        private static string _currentStep = "";
        private static DateTime _stepStartedAt = DateTime.MinValue;
        private static DateTime _nextInteract = DateTime.MinValue;
        private static bool _retainerStarted;
        private static DateTime? _idleSince;
        private static DateTime _workingSince = DateTime.MinValue;

        /// <summary>設定画面に出す直近の結果</summary>
        public static string LastResult { get; private set; } = "";

        public static bool HasBellHere() => BellLocations.ContainsKey(Svc.ClientState.TerritoryType);

        /// <summary>回収できるベンチャーがあるか(一定間隔でだけ AutoRetainer に聞く)</summary>
        public static AutoRetainerIPC.VentureState CheckVentureState(TimeSpan? minInterval = null)
        {
            var interval = minInterval ?? VentureCheckInterval;
            if (DateTime.Now - _lastVentureCheck >= interval)
            {
                _lastVentureState = P.AutoRetainer.CheckCollectableVenture();
                _lastVentureCheck = DateTime.Now;
            }
            return _lastVentureState;
        }

        /// <summary>HubActivityCheck から呼ばれる。今、拠点で回収に行くべきか</summary>
        public static bool ShouldCollect()
        {
            if (!C.Venture_Collect)
                return false;
            // 宇宙探査エリアの拠点でのみ行う
            if (!PlayerHelper.IsInCosmicZone())
                return false;
            if (!P.AutoRetainer.Installed)
            {
                if (EzThrottler.Throttle("Venture: AutoRetainer missing", 60000))
                    IceLogging.Warning("ベンチャー回収が有効ですが AutoRetainer が導入されていないため行いません", Tag);
                return false;
            }
            if (!HasBellHere())
                return false;
            if (DateTime.Now < _nextAttemptAllowedAt)
            {
                if (EzThrottler.Throttle("Venture: cooldown", 60000))
                    IceLogging.Verbose($"ベンチャー回収は {(_nextAttemptAllowedAt - DateTime.Now).TotalMinutes:F0} 分後まで試しません", Tag);
                return false;
            }
            var state = CheckVentureState();
            IceLogging.Verbose($"ベンチャーの状態: {state}", Tag);
            return state == AutoRetainerIPC.VentureState.Collectable;
        }

        public static void Enqueue()
        {
            P.TaskManager.EnqueueMulti
            (
                new(Begin, "Venture: begin"),
                new(WalkToBell, "Venture: walking to the summoning bell", Utils.TaskConfig),
                new(InteractWithBell, "Venture: opening the summoning bell", Utils.TaskConfig),
                new(WaitForAutoRetainer, "Venture: waiting for AutoRetainer", Utils.TaskConfig),
                new(CloseRetainerWindows, "Venture: closing retainer windows", Utils.TaskConfig),
                new(Finish, "Venture: finish")
            );
        }

        // ------------------------------------------------------------------

        private static bool? Begin()
        {
            _failed = false;
            _failReason = "";
            _beganAt = DateTime.Now;
            _currentStep = "";
            _nextInteract = DateTime.MinValue;
            _retainerStarted = false;
            _idleSince = null;
            _workingSince = DateTime.MinValue;
            IceLogging.Info("ベンチャー回収を始めます(拠点の呼び鈴へ向かいます)", Tag);
            IceLogging.ChatInfo(Loc.T("Ventures are ready. Heading to the summoning bell."), "[I.C.E.]");
            return true;
        }

        private static bool? WalkToBell()
        {
            if (_failed) return true;
            var elapsed = StepElapsed("walk");
            if (OverallExpired()) return Fail(Loc.T("Timed out"));

            var bell = FindBell();
            Vector3 target;
            if (bell != null)
                target = bell.Position;
            else if (!BellLocations.TryGetValue(Svc.ClientState.TerritoryType, out target))
                return Fail(Loc.T("No summoning bell is registered for this hub"));

            float distance = Player.DistanceTo(target);
            if (bell != null && distance <= InteractRange)
            {
                StopNavmesh();
                IceLogging.Debug($"呼び鈴の対話範囲に入りました({distance:F1}m)", Tag);
                return true;
            }
            if (bell == null && distance <= ArrivedRange)
            {
                // 覚えている場所に着いたのに見えない。読み込みが遅れているだけのことがあるので少し待つ
                StopNavmesh();
                if (elapsed > SearchGrace)
                    return Fail(Loc.T("No summoning bell nearby") + $" ({DescribeNearby()})");
                return false;
            }
            if (elapsed > TimeSpan.FromSeconds(120))
                return Fail(Loc.T("Could not reach the summoning bell") + $" ({distance:F1}m)");

            if (EzThrottler.Throttle("Venture walk log", 5000))
                IceLogging.Verbose($"呼び鈴へ向かっています(あと {distance:F1}m, 見えている={bell != null})", Tag);
            // 到着すると navmesh を止めて true を返す。ここでは距離判定を自前で行うので戻り値は使わない
            Task_NavmeshMove.Task_NavTo(target, true, MoveStopDistance, false, target);
            return false;
        }

        private static bool? InteractWithBell()
        {
            if (_failed) return true;
            var elapsed = StepElapsed("interact");
            if (OverallExpired()) return Fail(Loc.T("Timed out"));

            // 成功は状態の変化で確かめる(撃った回数では数えない)
            if (IsBellSessionOpen())
            {
                IceLogging.Info("呼び鈴を開きました。AutoRetainer の処理を待ちます", Tag);
                _workingSince = DateTime.Now;
                return true;
            }
            if (elapsed > TimeSpan.FromSeconds(60))
                return Fail(Loc.T("Could not open the summoning bell"));
            if (DateTime.Now < _nextInteract)
                return false;
            _nextInteract = DateTime.Now.AddSeconds(2);

            var bell = FindBell();
            if (bell == null)
            {
                if (elapsed > SearchGrace)
                    return Fail(Loc.T("No summoning bell nearby") + $" ({DescribeNearby()})");
                return false;
            }
            var distance = Player.DistanceTo(bell.Position);
            if (distance > InteractRange)
            {
                // 届いていなければ近寄り直す(失敗にしない)
                Task_NavmeshMove.Task_NavTo(bell.Position, true, MoveStopDistance, false, bell.Position);
                return false;
            }
            StopNavmesh();
            if (GenericHelpers.IsOccupied() || Player.IsBusy)
                return false;

            IceLogging.Debug($"呼び鈴に話しかけます({bell.Name}, {distance:F1}m)", Tag);
            Utils.TargetgameObject(bell);
            Utils.InteractWithObject(bell);
            return false;
        }

        private static bool? WaitForAutoRetainer()
        {
            if (_failed) return true;
            var elapsed = StepElapsed("working");

            // OccupiedSummoningBell だけで判定しない(一覧表示中に落ちることがある)。画面の表示も合わせて見る
            if (!IsBellSessionOpen())
            {
                IceLogging.Info("リテイナーの画面が閉じられました", Tag);
                return true;
            }
            if (elapsed > TimeSpan.FromMinutes(3) || OverallExpired())
            {
                IceLogging.Warning("リテイナーの処理が長引いているため画面を閉じます", Tag);
                return true;
            }
            if (!P.AutoRetainer.Installed)
                return true;
            if (!P.AutoRetainer.TryIsBusy(out var busy))
                return false;

            if (busy)
            {
                _retainerStarted = true;
                _idleSince = null;
                return false;
            }

            // 動き出す前の「忙しくない」は無視する
            if (!_retainerStarted)
            {
                if (CheckVentureState(TimeSpan.FromSeconds(1)) == AutoRetainerIPC.VentureState.None)
                {
                    IceLogging.Info("回収するものがありませんでした", Tag);
                    return true;
                }
                if (DateTime.Now - _workingSince < StartGrace)
                    return false;
                // 回収できるはずなのに動かない。AutoRetainer 側の「呼び鈴を開いたときの動作」が無効化されている等。
                // 次の区切りでまた開いても同じなので、失敗扱いにしてクールダウンを入れる
                _failed = true;
                _failReason = Loc.T("AutoRetainer did not start collecting");
                IceLogging.Warning("回収できるはずですが AutoRetainer が動き出しませんでした。AutoRetainer の「呼び鈴を開いたときの動作(ベンチャーあり)」が「AutoRetainer を有効化」になっているか確認してください", Tag);
                return true;
            }

            // リテイナー 1 人ずつ処理する合間に一瞬「忙しくない」になる。落ち着いてから閉じる
            _idleSince ??= DateTime.Now;
            var state = CheckVentureState(TimeSpan.FromSeconds(1));
            if (state != AutoRetainerIPC.VentureState.None)
            {
                // まだ残っている(または分からない)間は閉じない
                _idleSince = null;
                return false;
            }
            if (DateTime.Now - _idleSince.Value < IdleBeforeClose)
                return false;

            IceLogging.Info("回収できるベンチャーが無くなりました。画面を閉じます", Tag);
            return true;
        }

        private static bool? CloseRetainerWindows()
        {
            // 失敗していても、画面が開いていれば必ず閉じる(開いたままだとミッションへ戻れない)
            var elapsed = StepElapsed("closing");
            if (!IsBellSessionOpen())
                return true;
            if (elapsed > TimeSpan.FromSeconds(30))
                return Fail(Loc.T("Could not close the retainer windows"));
            if (DateTime.Now < _nextInteract)
                return false;
            _nextInteract = DateTime.Now.AddSeconds(2);

            // AutoRetainer が動いていると閉じる操作と取り合うので先に止める
            if (P.AutoRetainer.TryIsBusy(out var busy) && busy)
            {
                P.AutoRetainer.TryAbort();
                return false;
            }
            CloseAddons();
            return false;
        }

        private static bool? Finish()
        {
            StopNavmesh();
            _lastVentureCheck = DateTime.MinValue;
            if (_failed)
            {
                _consecutiveFailures++;
                var cooldown = _consecutiveFailures < FailuresBeforeLongWait
                    ? ShortRetryDelay
                    : TimeSpan.FromMinutes(Math.Max(1, C.Venture_RetryCooldownMinutes));
                _nextAttemptAllowedAt = DateTime.Now.Add(cooldown);
                LastResult = $"{DateTime.Now:HH:mm} {Loc.T("Failed")}: {_failReason} ({Loc.T("retry in")} {cooldown.TotalMinutes:F0} {Loc.T("min")})";
                IceLogging.Warning($"ベンチャー回収に失敗: {_failReason}。{cooldown.TotalMinutes:F0} 分後まで試しません(連続 {_consecutiveFailures} 回)", Tag);
                IceLogging.ChatError(Loc.T("Venture collection failed, so ICE will try again later."), "[I.C.E.]");
            }
            else
            {
                _consecutiveFailures = 0;
                _nextAttemptAllowedAt = DateTime.Now.Add(AfterSuccessDelay);
                LastResult = $"{DateTime.Now:HH:mm} {Loc.T("Collected")}";
                IceLogging.Info("ベンチャー回収を終えました", Tag);
                IceLogging.ChatInfo(Loc.T("Ventures were collected."), "[I.C.E.]");
            }
            return true;
        }

        // ------------------------------------------------------------------

        private static bool? Fail(string reason)
        {
            _failed = true;
            _failReason = reason;
            StopNavmesh();
            IceLogging.Warning($"ベンチャー回収を中断: {reason}", Tag);
            return true;
        }

        private static TimeSpan StepElapsed(string step)
        {
            if (_currentStep != step)
            {
                _currentStep = step;
                _stepStartedAt = DateTime.Now;
            }
            return DateTime.Now - _stepStartedAt;
        }

        private static bool OverallExpired() => DateTime.Now - _beganAt > OverallLimit;

        private static void StopNavmesh()
        {
            try
            {
                if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                    P.Navmesh.Stop();
            }
            catch { }
        }

        /// <summary>呼び鈴を使っている最中か。フラグと関連画面の表示の両方を見る</summary>
        public static bool IsBellSessionOpen()
        {
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
                return true;
            foreach (var name in RetainerAddons)
            {
                if (AddonHelper.IsAddonActive(name))
                    return true;
            }
            return false;
        }

        private static unsafe void CloseAddons()
        {
            foreach (var name in RetainerAddons)
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible)
                {
                    IceLogging.Debug($"{name} を閉じます", Tag);
                    addon->Close(true);
                }
            }
        }

        /// <summary>
        /// 現在地から一番近い呼び鈴を探す(読み込まれているものだけ)。正式名と完全一致するものを優先し、
        /// 「美容師の呼び鈴」など無関係なものは除外する。IsTargetable でないものは対象外。
        /// </summary>
        public static IGameObject? FindBell()
        {
            string bellName = "";
            try
            {
                bellName = Svc.Data.GetExcelSheet<EObjName>()?.GetRowOrDefault(BellEObjNameRow)?.Singular.ExtractText() ?? "";
            }
            catch { }

            IGameObject? nearest = null;
            float nearestDistance = float.MaxValue;
            bool nearestIsExact = false;
            var me = Player.Position;

            foreach (var obj in Svc.Objects)
            {
                if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                    continue;
                var name = obj.Name.ToString();
                if (IsDecoyBell(name))
                    continue;
                bool exact = bellName.Length > 0 && string.Equals(name, bellName, StringComparison.Ordinal);
                bool matches = exact
                               || (bellName.Length > 0 && name.Contains(bellName, StringComparison.Ordinal))
                               || name.Contains("呼び鈴", StringComparison.Ordinal)
                               || name.Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase);
                if (!matches || !obj.IsTargetable)
                    continue;
                var distance = Vector3.Distance(obj.Position, me);
                bool better = nearest == null
                              || (exact && !nearestIsExact)
                              || (exact == nearestIsExact && distance < nearestDistance);
                if (!better)
                    continue;
                nearest = obj;
                nearestDistance = distance;
                nearestIsExact = exact;
            }
            return nearest;
        }

        private static bool IsDecoyBell(string name)
            => name.Contains("美容師", StringComparison.Ordinal)
               || name.Contains("Aesthetician", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Crystal Bell", StringComparison.OrdinalIgnoreCase);

        private static string DescribeNearby()
        {
            var me = Player.Position;
            var near = Svc.Objects
                .Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject)
                .Select(x => (Name: x.Name.ToString(), Distance: Vector3.Distance(x.Position, me)))
                .Where(x => x.Distance <= 15f)
                .OrderBy(x => x.Distance)
                .Take(4)
                .Select(x => $"{x.Name} {x.Distance:F1}m")
                .ToList();
            return near.Count == 0 ? "near: nothing" : "near: " + string.Join(" / ", near);
        }
    }
}
