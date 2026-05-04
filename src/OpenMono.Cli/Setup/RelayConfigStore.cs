using System.Text.Json;

namespace OpenMono.Setup;

public static class RelayConfigStore
{
    private static string GetPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "relay.json");

    public static RelayConfig? Load(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RelayConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string dataDirectory, RelayConfig config)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = GetPath(dataDirectory);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // owner-only, no group/other read
    }
}
