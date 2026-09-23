using System.Collections.Generic;

namespace ICE.ConfigFiles;

public partial class Config
{
    /// <summary>オーバーレイを自動で開く(既定 true)。宇宙探査エリアに入ったとき/プラグイン読み込み時に開く。</summary>
    public bool ShowOverlay { get; set; } = true;
    /// <summary>ShowOverlay の既定値を true に変えた際の一回限りの移行フラグ(旧設定の false を一度だけ true に引き上げる)。</summary>
    public bool Overlay_AutoOpenDefaultApplied { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowCurrentScore { get; set; } = true;
    public bool ShowTotalScore { get; set; } = true;
    public bool ShowMasteryScore { get; set; } = true;
    public bool ShowExpBars { get; set; } = true;
    public bool ShowExpBars_HideWhenMaxed { get; set; } = false;
    public bool Overlay_AutoResize { get; set; } = true;
    public bool Overlay_FilterByJob { get; set; } = false;
    public bool Overlay_FilterByCurrentJob { get; set; } = false;
    public HashSet<uint> Overlay_FilterJobs { get; set; } = new();
    public bool Overlay_HighlightTokenWeather { get; set; } = true;
    public bool Overlay_UseCogsIcon { get; set; } = false;
    public bool Overlay_RelicXpExpanded { get; set; } = false;
    public bool Overlay_WeatherSelected { get; set; } = true;
}
