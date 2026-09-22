using System.Threading.Tasks;

namespace ICE.OldYamlConfig;

public interface IYamlConfig
{
    Task SaveAsync();
    void Save();
    static abstract string ConfigPath { get; }
}
