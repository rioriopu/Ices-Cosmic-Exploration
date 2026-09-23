using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ICE.Ui.Waypoint_Manager;

public class PathFile
{
    public string PathName { get; set; } = "default_path";
    public List<WaypointUtil> Waypoints { get; set; } = new();
}

public class WaypointUtil
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public bool Jump { get; set; } = false;

    public Vector3 ToVector3() => new(X, Y, Z);

    public static WaypointUtil FromVector3(Vector3 vec, bool jump = false)
        => new() { X = vec.X, Y = vec.Y, Z = vec.Z, Jump = jump };
}

public class PathManager
{
    private readonly string _folderPath;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public PathManager()
    {
        // Save to: %AppData%/XIVLauncher/pluginConfigs/ICE/Paths
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "pluginConfigs",
            "ICE",
            "Paths"
        );

        _folderPath = configDir;

        Directory.CreateDirectory(_folderPath); // Ensure folder exists

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public void Save(PathFile pathFile)
    {
        string yaml = _serializer.Serialize(pathFile);
        File.WriteAllText(Path.Combine(_folderPath, $"path_{pathFile.PathName}.yaml"), yaml);
    }

    public PathFile? Load(string name)
    {
        string fullPath = Path.Combine(_folderPath, $"path_{name}.yaml");
        if (!File.Exists(fullPath))
            return null;

        string yaml = File.ReadAllText(fullPath);
        return _deserializer.Deserialize<PathFile>(yaml);
    }

    public bool Rename(string oldName, string newName)
    {
        string oldPath = Path.Combine(_folderPath, $"path_{oldName}.yaml");
        string newPath = Path.Combine(_folderPath, $"path_{newName}.yaml");

        if (!File.Exists(oldPath) || File.Exists(newPath))
            return false; // Can't rename if old doesn't exist or new already exists

        File.Move(oldPath, newPath);

        // Optional: Update the PathName inside the file
        var pathFile = Load(newName);
        if (pathFile != null)
        {
            pathFile.PathName = newName;
            Save(pathFile); // Overwrite with updated name inside
        }

        return true;
    }


    public List<string?> ListAllPaths()
    {
        return Directory.GetFiles(_folderPath, "path_*.yaml")
                        .Select(Path.GetFileNameWithoutExtension)
                        .ToList();
    }

    public void Delete(string name)
    {
        string fullPath = Path.Combine(_folderPath, $"path_{name}.yaml");
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }
}
