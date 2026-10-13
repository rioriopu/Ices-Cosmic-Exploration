using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICE.Scheduler.Tasks;
using SmoothNav.Core;

Console.OutputEncoding = Encoding.UTF8;
var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));
void Check(bool ok, string why = "条件が一致しない") { if (!ok) throw new Exception(why); }

Func<float, float, float> flat = (_, _) => 0;
RectMesh Open() => new([(-100, 100, -100, 100, flat)]);
Vector3 start = new(0, 0, 0), corner = new(20, 0, 0), end = new(20, 0, 20);

// 探索が終わり、整え終わるまで Gate を回す(現実の時間で 5 秒まで)。now は整える予算(0.25 秒)の内側に置く
SmoothGate Settle(SmoothNavDriver d, Vector3 position, Vector3 goal, double now = .1)
{
    var clock = Stopwatch.StartNew();
    while (true)
    {
        var gate = d.Gate(position, goal, false, false, now);
        if (gate != SmoothGate.Planning)
            return gate;
        if (clock.Elapsed.TotalSeconds > 5)
            throw new Exception("5 秒たっても Planning のまま");
        Thread.Sleep(5);
    }
}

// 経路を頼み、公式の探索の結果を返し、整え終わるまで回す
SmoothGate Run(FakePort port, SmoothNavDriver d, List<Vector3> official)
{
    Check(d.TryBegin(official[0], official[^1], 0, out var reason), "頼めなかった: " + reason);
    port.Search.SetResult(official);
    return Settle(d, official[0], official[^1]);
}

Test("頼めないときは false(vnavmesh 任せの探索が保留中・探索を頼めない・探索の例外)", () =>
{
    var port = new FakePort(Open()) { SimplePending = true };
    var d = new SmoothNavDriver(port);
    Check(!d.TryBegin(start, end, 0, out var r1) && r1.Contains("保留中") && port.PathfindCalls == 0, r1);
    port.SimplePending = false;
    port.ReturnNull = true;
    Check(!d.TryBegin(start, end, 0, out var r2) && r2.Length > 0 && !d.Planning, r2);
    port.ReturnNull = false;
    port.Throw = new InvalidOperationException();
    Check(!d.TryBegin(start, end, 0, out var r3) && r3.Contains(nameof(InvalidOperationException)) && !d.Planning, r3);
    Check(port.Moves.Count == 0);
});

Test("探索中は Planning のままで、経路は渡さない", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(d.TryBegin(start, end, 0, out _) && d.Planning);
    for (var i = 0; i < 3; i++)
        Check(d.Gate(start, end, false, false, i * .1) == SmoothGate.Planning);
    Check(port.Moves.Count == 0);
});

Test("L 字の経路の角を曲線に整えて渡す(出発点は含めず、終点は公式の終点)", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(Run(port, d, [start, corner, end]) == SmoothGate.Applied, d.LastSummary);
    var moved = port.Moves.Single();
    Check(moved[^1] == end, "終点が公式の終点でない");
    Check(!moved.Contains(corner) && moved[0] != start, "角が残っているか、出発点を含めた: " + string.Join(" ", moved));
    Check(d.LastSummary.StartsWith("曲線に整えた経路"), d.LastSummary);
    // 渡した後は vnavmesh の追従に任せる(Gate は何もしない)
    Check(!d.Planning && d.Gate(start, end, false, false, .2) == SmoothGate.None && port.Moves.Count == 1);
});

Test("既に終点にいる(公式の経路が 1 点)なら何も渡さず、印も付けない", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(Run(port, d, [start]) == SmoothGate.None && port.Moves.Count == 0, d.LastSummary);
    Check(d.TryBegin(start, start, 1, out var r), "印が付いた: " + r);
});

Test("公式の経路が空なら vnavmesh 任せへ戻し、その目的地に印を付ける", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(d.TryBegin(start, end, 0, out _));
    port.Search.SetResult([]);
    Check(Settle(d, start, end) == SmoothGate.None && port.Moves.Count == 0);
    Check(d.LastSummary.Contains("vnavmesh 任せへ戻す") && d.LastSummary.Contains("公式の経路が空"), d.LastSummary);
    Check(!d.TryBegin(start, end, 1, out var r) && r.Contains("公式の経路で動かす"), r);
    Check(!d.TryBegin(start, end + new Vector3(1, 0, 0), 1, out _), "目的地の揺れ(1m)で印を素通りした");
    Check(d.TryBegin(start, new Vector3(50, 0, 50), 1, out _), "別の目的地まで公式に戻した");
});

Test("公式の探索の例外も vnavmesh 任せへ戻す", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(d.TryBegin(start, end, 0, out _));
    port.Search.SetException(new InvalidOperationException("探索の失敗"));
    Check(Settle(d, start, end) == SmoothGate.None && port.Moves.Count == 0);
    Check(d.LastSummary.Contains("vnavmesh 任せへ戻す") && d.LastSummary.Contains("例外"), d.LastSummary);
});

Test("歩ける面が途切れる区間(コスモライナーの乗り口などのリンク)を含む経路は vnavmesh 任せへ戻す", () =>
{
    // x 10〜14 が面の無い隙間。公式の経路は隙間をまたぐ(リンクで渡る)
    var port = new FakePort(new RectMesh([(-100, 10, -100, 100, flat), (14, 100, -100, 100, flat)]));
    var d = new SmoothNavDriver(port);
    Check(Run(port, d, [start, new(8, 0, 0), new(16, 0, 0), new(30, 0, 0)]) == SmoothGate.None && port.Moves.Count == 0);
    Check(d.LastSummary.Contains("リンクの疑い") && d.LastSummary.Contains("区間 2/3"), d.LastSummary);
    Check(!d.TryBegin(start, new Vector3(30, 0, 0), 1, out _), "リンクのある目的地に印が付いていない");
});

Test("見逃し: 第 1 区間の始点(自機)が面の少し外", () =>
{
    var port = new FakePort(new RectMesh([(-100, 100, 0, 100, flat)]));
    var d = new SmoothNavDriver(port);
    var off = new Vector3(0, 0, -.3f);
    Check(Run(port, d, [off, new(20, 0, 1), new(20, 0, 20)]) == SmoothGate.Applied, d.LastSummary);
});

Test("見逃し: 最終区間の終点が面の少し外(1.5m 以内)。遠ければ戻す", () =>
{
    var mesh = new RectMesh([(-100, 20, -100, 100, flat)]);
    var port = new FakePort(mesh);
    var d = new SmoothNavDriver(port);
    Check(Run(port, d, [start, new(10, 0, 5), new(21, 0, 5)]) == SmoothGate.Applied, d.LastSummary);
    Check(port.Moves.Single()[^1] == new Vector3(21, 0, 5), "終点が公式の終点でない");
    var port2 = new FakePort(mesh);
    var d2 = new SmoothNavDriver(port2);
    Check(Run(port2, d2, [start, new(10, 0, 5), new(25, 0, 5)]) == SmoothGate.None && port2.Moves.Count == 0, d2.LastSummary);
    Check(d2.LastSummary.Contains("リンクの疑い"), d2.LastSummary);
});

Test("見逃し: 最終区間の終点が別の高さ(差 3m 以内)。差が大きければ戻す", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(Run(port, d, [start, new(10, 0, 0), new(20, 2.5f, 0)]) == SmoothGate.Applied, d.LastSummary);
    var port2 = new FakePort(Open());
    var d2 = new SmoothNavDriver(port2);
    Check(Run(port2, d2, [start, new(10, 0, 0), new(20, 6, 0)]) == SmoothGate.None && port2.Moves.Count == 0, d2.LastSummary);
});

Test("目的地の揺れ(1.5m 以内)は同じ移動として続け、目的地が変われば取り消す", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(d.TryBegin(start, end, 0, out _));
    Check(d.Gate(start, end + new Vector3(1, 0, 0), false, false, .1) == SmoothGate.Planning, "揺れで取り消した");
    Check(d.Gate(start, end + new Vector3(5, 0, 0), false, false, .1) == SmoothGate.None && !d.Planning, "目的地が変わっても続けた");
    port.Search.SetResult([start, corner, end]);
    Thread.Sleep(50);
    Check(d.Gate(start, end, false, false, .2) == SmoothGate.None && port.Moves.Count == 0, "取り消した計画を渡した");
});

Test("着いた・乗り物に乗った・計画中に動いた・他の移動が走り出したら取り消す(1m 動いただけなら続ける)", () =>
{
    var cases = new (string Name, Func<SmoothNavDriver, FakePort, SmoothGate> Gate, SmoothGate Expected)[]
    {
        ("着いた", (d, _) => d.Gate(start, end, true, false, .1), SmoothGate.None),
        ("乗り物", (d, _) => d.Gate(start, end, false, true, .1), SmoothGate.None),
        ("3m 動いた", (d, _) => d.Gate(start + new Vector3(3, 0, 0), end, false, false, .1), SmoothGate.None),
        ("他の移動", (d, p) => { p.Running = true; return d.Gate(start, end, false, false, .1); }, SmoothGate.None),
        ("1m 動いた", (d, _) => d.Gate(start + new Vector3(1, 0, 0), end, false, false, .1), SmoothGate.Planning),
    };
    foreach (var c in cases)
    {
        var port = new FakePort(Open());
        var d = new SmoothNavDriver(port);
        Check(d.TryBegin(start, end, 0, out _));
        Check(c.Gate(d, port) == c.Expected, c.Name);
        if (c.Expected != SmoothGate.None)
            continue;
        port.Running = false;
        port.Search.SetResult([start, corner, end]);
        Thread.Sleep(50);
        Check(d.Gate(start, end, false, false, .2) == SmoothGate.None && port.Moves.Count == 0, c.Name + ": 取り消した計画を渡した");
    }
});

Test("Cancel の後は何も渡さない", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(d.TryBegin(start, end, 0, out _));
    d.Cancel();
    Check(!d.Planning);
    port.Search.SetResult([start, corner, end]);
    Thread.Sleep(50);
    Check(d.Gate(start, end, false, false, .1) == SmoothGate.None && port.Moves.Count == 0);
});

Test("公式に戻す印は 60 秒で切れ、着いたら外せる", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    d.MarkOfficial(end, 0, "詰まった");
    Check(!d.TryBegin(start, end, 10, out var r) && r.Contains("詰まった"), r);
    Check(d.TryBegin(start, end, 61, out _), "印が切れていない");
    d.Cancel();
    d.MarkOfficial(end, 100, "詰まった");
    d.ClearMarks(end + new Vector3(.5f, 0, 0));
    Check(d.TryBegin(start, end, 101, out _), "印を外せていない");
});

Test("整える処理が 0.25 秒に間に合わなければ公式の経路を渡し、整える処理は取り消す", () =>
{
    var port = new FakePort(Open());
    var slow = new SlowRefiner();
    var d = new SmoothNavDriver(port, slow);
    Check(d.TryBegin(start, end, 0, out _));
    port.Search.SetResult([start, corner, end]);
    var clock = Stopwatch.StartNew();
    while (!slow.Started.IsSet)
    {
        Check(d.Gate(start, end, false, false, 0) == SmoothGate.Planning, "整え始める前に計画が終わった");
        Check(clock.Elapsed.TotalSeconds < 5, "整える処理が始まらない");
        Thread.Sleep(5);
    }
    Check(d.Gate(start, end, false, false, .3) == SmoothGate.Applied, d.LastSummary);
    Check(port.Moves.Single().SequenceEqual([corner, end]), "公式の経路(出発点を除く)でない");
    Check(d.LastSummary.StartsWith("公式の経路") && d.LastSummary.Contains("時間内に終わらない"), d.LastSummary);
    Check(slow.Cancelled.Wait(2000), "整える処理を取り消していない");
});

Test("整え終わったときに vnavmesh 任せの探索が保留中なら、経路を渡さない", () =>
{
    var port = new FakePort(Open());
    var d = new SmoothNavDriver(port);
    Check(d.TryBegin(start, end, 0, out _));
    port.SimplePending = true;
    port.Search.SetResult([start, corner, end]);
    Check(Settle(d, start, end) == SmoothGate.None && port.Moves.Count == 0);
    Check(d.LastSummary.Contains("保留中"), d.LastSummary);
});

var failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}
Console.WriteLine($"単体試験 {tests.Count - failed}/{tests.Count} 合格");
return failed == 0 ? 0 : 1;

/// <summary>vnavmesh の代わり。探索は TaskCompletionSource で、呼び出し側が結果を返す。</summary>
internal sealed class FakePort(IMeshPointQuery mesh) : ISmoothNavPort
{
    public bool SimplePending, Running, ReturnNull;
    public Exception? Throw;
    public int PathfindCalls;
    public TaskCompletionSource<List<Vector3>> Search = new();
    public readonly List<List<Vector3>> Moves = new();

    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to)
    {
        PathfindCalls++;
        if (Throw != null)
            throw Throw;
        if (ReturnNull)
            return null;
        Search = new TaskCompletionSource<List<Vector3>>();
        return Search.Task;
    }

    public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY) => mesh.NearestPoint(point, halfExtentXZ, halfExtentY);
    public bool PathIsRunning() => Running;
    public bool SimpleMovePending() => SimplePending;
    public void MoveAlong(List<Vector3> points) => Moves.Add(points);
}

/// <summary>取り消されるまで終わらない整える処理(予算切れの試験用)。</summary>
internal sealed class SlowRefiner : IPathRefiner
{
    public readonly ManualResetEventSlim Started = new();
    public readonly ManualResetEventSlim Cancelled = new();

    public CurveResult Refine(IReadOnlyList<Vector3> official, bool fly, CancellationToken cancel)
    {
        Started.Set();
        cancel.WaitHandle.WaitOne(5000);
        if (cancel.IsCancellationRequested)
            Cancelled.Set();
        cancel.ThrowIfCancellationRequested();
        throw new TimeoutException();
    }
}

/// <summary>高さの関数を持つ四角の組み合わせ。範囲内で最も近い面の上の点を返す(SmoothNav の試験と同じ作り)。</summary>
internal sealed class RectMesh(List<(float MinX, float MaxX, float MinZ, float MaxZ, Func<float, float, float> Height)> rects) : IMeshPointQuery
{
    public Vector3? NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY)
    {
        Vector3? best = null;
        var bestDistance = float.MaxValue;
        foreach (var r in rects)
        {
            var x = Math.Clamp(p.X, r.MinX, r.MaxX);
            var z = Math.Clamp(p.Z, r.MinZ, r.MaxZ);
            var q = new Vector3(x, r.Height(x, z), z);
            if (Math.Abs(q.X - p.X) > halfExtentXZ || Math.Abs(q.Z - p.Z) > halfExtentXZ || Math.Abs(q.Y - p.Y) > halfExtentY)
                continue;
            var d = Vector3.Distance(q, p);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = q;
            }
        }
        return best;
    }
}
