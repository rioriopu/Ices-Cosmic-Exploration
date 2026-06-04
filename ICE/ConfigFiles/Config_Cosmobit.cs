namespace ICE.ConfigFiles;

public partial class Config
{
    public bool Cosmodrone_Buy { get; set; } = true;
    public int Cosmodrone_BuyAt { get; set; } = 4000;
    public int Cosmodrone_MaxKeep { get; set; } = 0;
    public bool Cosmodrone_Run { get; set; } = false;

    // ON にすると、有効化したマスターシップミッションがある間はドローン探索に譲らず、マスターを優先して受注する。
    // 既定OFF=従来どおりドローン優先(掘削+鑑定を中断しない)。ONにするとマスターgrabがドローン鑑定を中断しうる点に注意。
    public bool MasterPriorityOverDrone { get; set; } = false;
}
