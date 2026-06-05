
using System.Collections.Generic;

namespace ICE.ConfigFiles;

public partial class Config
{
    public Dictionary<string, bool> MainUi_CustomHeader { get; set; } = new();
    // 既定タブは Mission Setup。空文字だと MainBody が "Hehe" プレースホルダを表示し、
    // 起動経路(OpenMainUi)によっては初期画面で Mission Setup が出ない不具合になっていた。
    public string MainUi_SelectedWindow { get; set; } = "modeSelect_MissionSetup";
    public Dictionary<string, bool> Mission_Tabs { get; set; } = new();
}
