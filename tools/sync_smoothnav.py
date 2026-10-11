# 移動の角を曲線に整える部品 SmoothNav(別リポジトリ)の写しを ICE/SmoothNav へ取り込む。
#
# SmoothNav は別リポジトリの部品。ICE は原本を参照せず、地上の経路を整えるのに必要なファイルだけを写して一緒に組み立てる。
# 写しは直接直さず、原本で直してからこの台本で写し直す。
#
# 使い方:
#   python -X utf8 tools/sync_smoothnav.py [原本のリポジトリ] [--commit <コミット>]
#   python -X utf8 tools/sync_smoothnav.py --verify [原本のリポジトリ]   # 写しと原本が一致するかだけを調べる
# 原本の既定は \\100.99.141.112\ソース\SmoothNav-window-fixes(git の worktree。この PC からは git で読めないことがあるので、
# その場合は --commit で原本のコミットを渡す)。
import hashlib
import os
import subprocess
import sys

args = [a for a in sys.argv[1:]]
verify = "--verify" in args
if verify:
    args.remove("--verify")
commit = None
if "--commit" in args:
    i = args.index("--commit")
    commit = args[i + 1]
    del args[i:i + 2]
source = args[0] if args else r"\\100.99.141.112\ソース\SmoothNav-window-fixes"

here = os.path.dirname(os.path.abspath(__file__))
destination = os.path.normpath(os.path.join(here, "..", "ICE", "SmoothNav", "Core"))
# 宇宙探査は地上の移動だけなので、地上の経路を整える部品(RoutePlanner)に要るファイルだけを写す。
# 飛行用(GameCollisionProbe・FrameworkQueue・LoadedCoverage)と記録・移動の管理役(SmoothNavigator)は写さない。
names = ["Collision", "CurveBuilder", "FollowerSimulation", "GroundSurface", "MeshPointGround", "Metrics",
         "MovementSession", "PathMarker", "PathRefiner", "RoutePlanner", "WindowMetrics"]
# ICE は暗黙の using を使わないので、原本が暗黙に使っている名前空間を足す。
usings = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Threading", "System.Threading.Tasks"]


def original_text(name):
    path = os.path.join(source, "SmoothNav.Core", name + ".cs")
    return open(path, encoding="utf-8-sig").read().replace("\r\n", "\n")


def digest(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


if verify:
    bad = []
    for name in names:
        copied = open(os.path.join(destination, name + ".cs"), encoding="utf-8").read().replace("\r\n", "\n")
        body = copied.split("\n")
        # 1 行目(出所)と、足した using を除いた残りが原本と同じか
        while body and (body[0].startswith("// SmoothNav") or (body[0].startswith("using ") and body[0][6:-1] in usings)):
            body = body[1:]
        if "\n".join(body) != original_text(name):
            bad.append(name)
    if bad:
        sys.exit("写しと原本が一致しません: " + ", ".join(bad))
    print(f"{len(names)} ファイルとも原本と一致しました")
    sys.exit(0)

if commit is None:
    try:
        commit = subprocess.run(["git", "-C", source, "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        dirty = subprocess.run(["git", "-C", source, "status", "--porcelain", "--", "SmoothNav.Core"],
                               capture_output=True, text=True, check=True).stdout.strip()
        if dirty:
            sys.exit("原本に未コミットの変更があります。コミットしてから写してください:\n" + dirty)
    except (subprocess.CalledProcessError, FileNotFoundError):
        sys.exit("原本のコミットを git で読めません。--commit <コミット> で原本のコミットを渡してください")

os.makedirs(destination, exist_ok=True)
rows = []
for name in names:
    text = original_text(name)
    lines = text.split("\n")
    present = {l.strip()[6:-1] for l in lines if l.startswith("using ") and l.strip().endswith(";")}
    added = [f"using {u};" for u in usings if u not in present]
    header = [f"// SmoothNav(SmoothNav.Core/{name}.cs・{commit})の写し。ここでは直さず、原本で直して tools/sync_smoothnav.py で写し直す。"]
    with open(os.path.join(destination, name + ".cs"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header + added + lines))
    rows.append(f"| Core/{name}.cs | SmoothNav.Core/{name}.cs | {digest(text)} |")

readme = f"""# SmoothNav の写し

移動の角を曲線に整える部品 SmoothNav(別リポジトリ)の写し。原本のコミットは `{commit}`。

- ICE は宇宙探査の地上の移動だけで使う。使うのは経路だけを整える `RoutePlanner` と、vnavmesh の点の照会で作った歩ける面の窓口 `MeshPointGround`。
  つなぎは `Scheduler/Tasks/SmoothNavDriver.cs`(ゲームに触れない部品)と `Scheduler/Tasks/SmoothNavBridge.cs`(ICE につなぐ層)を見る。
- **ここのファイルは直接直さない。** 原本で直してから `python -X utf8 tools/sync_smoothnav.py` で写し直す
  (写すときに、ICE が暗黙に使わない using を足し、1 行目に原本を書く)。`--verify` で写しと原本が一致するかを確かめられる。

| 写し | 原本 | 原本の SHA256(先頭16文字・改行は LF) |
|---|---|---|
""" + "\n".join(rows) + "\n"
open(os.path.join(destination, "..", "README.md"), "w", encoding="utf-8", newline="\n").write(readme)
print(f"{len(rows)} ファイルを写しました(原本 {commit})→ {destination}")
