using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.ConfigFiles;

public partial class Config
{
    public bool TurninRelic { get; set; } = false;
    public bool Relic_SwapJob { get; set; } = false;
    public uint Relic_BattleJob { get; set; } = 0;
    public bool Relic_Stylist { get; set; } = true;
    // Relic Grind モードでは「納品(強化)→一時ジョブ切替→元のジョブへ戻る→最強装備」を、上の設定に関係なく自動で行う。
    // 一時ジョブは Relic_BattleJob が未設定/ギアセット無しなら、納品ジョブ以外でギアセットのある任意のジョブを自動で選ぶ。
    public bool Relic_AutoUpgradeInRelicMode { get; set; } = true;
    // Relic Grind モードで D〜B ランクの未達成ミッションを優先して受注する。
    // 同じ高効率ミッションばかり受けていると次ランクの解放条件(各ランクの達成数)を満たせず、上位ランクを受注できなくなるため。
    public bool Relic_PrioritizeIncomplete { get; set; } = true;

    public Dictionary<uint, bool> RelicJobs { get; set; } = new()
    {
        [8] = true,
        [9] = true,
        [10] = true,
        [11] = true,
        [12] = true,
        [13] = true,
        [14] = true,
        [15] = true,
        [16] = true,
        [17] = true,
        [18] = true
    };
    public bool FarmAllRelics { get; set; } = false;
    public bool Stop_AllRelicsComplete { get; set; } = false;
}
