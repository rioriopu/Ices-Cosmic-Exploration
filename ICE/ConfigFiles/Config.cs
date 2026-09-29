using ECommons.Configuration;

namespace ICE.ConfigFiles;

public partial class Config
{
    public int ConfigVersion = 1;
    public int Config_Versioning { get; set; } = 2;
    public bool OldConfigMigrateV1 = false;

    public void Save()
    {
        // 複数クライアント同時起動でも他のクライアントの変更を消さないよう、差分マージで保存する
        EzConfigExtensions.SaveMerged();
    }

    public void SaveDebounced()
    {
        EzConfigExtensions.SaveDebounced();
    }
}
