using ICE.Utilities.Cosmic_Helper;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public static class YamlConfig
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            var defaultConfig = new T();
            SaveSync(defaultConfig, path);
            return defaultConfig; // Return the default directly, don't deserialize
        }

        try
        {
            var yaml = File.ReadAllText(path);

            // Check if file is empty or whitespace only
            if (string.IsNullOrWhiteSpace(yaml))
            {
                PluginLog.Warning($"Config file at {path} is empty, creating default");
                var defaultConfig = new T();
                SaveSync(defaultConfig, path);
                return defaultConfig;
            }

            return Deserializer.Deserialize<T>(yaml) ?? new T();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Failed to deserialize config from {path}, creating default\n" +
                            $"{ex}");
            var defaultConfig = new T();
            SaveSync(defaultConfig, path);
            return defaultConfig;
        }
    }

    public static async Task SaveAsync<T>(T config, string path)
    {
        var yaml = Serializer.Serialize(config);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to temp file first
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, yaml).ConfigureAwait(false);

        // Check if destination file exists
        if (File.Exists(path))
        {
            var backupPath = path + ".bak";
            // Atomic replace with backup
            File.Replace(tempPath, path, backupPath, true);

            // Delete backup after successful save
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        else
        {
            // First time save - just move the temp file
            File.Move(tempPath, path);
        }
    }

    public static void SaveSync<T>(T config, string path)
    {
        var yaml = Serializer.Serialize(config);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to temp file first
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, yaml);

        // Check if destination file exists
        if (File.Exists(path))
        {
            var backupPath = path + ".bak";
            // Atomic replace with backup (creates .bak automatically)
            File.Replace(tempPath, path, backupPath, true);

            // Delete backup after successful save
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        else
        {
            // First time save - just move the temp file
            File.Move(tempPath, path);
        }
    }

    public static T LoadFromResource<T>(string resourceName) where T : new()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            PluginLog.Warning($"Could not find embedded resource: {resourceName}");
            return new T();
        }

        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        return Deserializer.Deserialize<T>(yaml) ?? new T();
    }
}