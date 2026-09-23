namespace ICE.ConfigFiles;

public partial class Config
{
    public bool Cosmodrone_Buy { get; set; } = true;
    public int Cosmodrone_BuyAt { get; set; } = 4000;
    public int Cosmodrone_MaxKeep { get; set; } = 0;
    public bool Cosmodrone_Run { get; set; } = false;
    /// <summary>ドローン探索でエネルギーパックを使い切った後、ドローンNPC(カエデ)で「古代の記録」を自動鑑定するか。false なら鑑定せず拠点へ戻るだけ。</summary>
    public bool Cosmodrone_AutoAppraise { get; set; } = true;
}
