namespace ICE.ConfigFiles;

public partial class Config
{
    /// <summary>
    /// リテイナーのベンチャーが回収可能になったら、ミッションの区切りで拠点へ戻り、呼び鈴を開いて AutoRetainer に回収させる。
    /// 宇宙探査エリア内でのみ動作する。初期値は OFF。
    /// </summary>
    public bool Venture_Collect { get; set; } = false;

    /// <summary>回収の失敗が 3 回続いたあと、次に試すまでの待ち時間(分)</summary>
    public int Venture_RetryCooldownMinutes { get; set; } = 30;
}
