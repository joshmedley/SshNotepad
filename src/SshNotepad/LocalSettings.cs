using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SshNotepad.Core;

namespace SshNotepad;

/// <summary>Per-computer settings (not synced).</summary>
static class LocalSettings
{
    /// <summary>SSHNOTEPAD_HOME lets a second, separate instance run for testing.</summary>
    public static string Home { get; } = Environment.GetEnvironmentVariable("SSHNOTEPAD_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshNotepad");

    /// <summary>SSHNOTEPAD_PC overrides the computer name, for testing.</summary>
    public static string Computer { get; } = Environment.GetEnvironmentVariable("SSHNOTEPAD_PC") ?? Environment.MachineName;

    private static string FilePath => Path.Combine(Home, "settings.json");

    public static ConnectionSettings? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(FilePath)) : null; }
        catch (JsonException) { return null; }
    }

    public static void Save(ConnectionSettings settings)
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Each host, login and folder has its own cache, so switching never mixes notes.</summary>
    public static string CacheFolder(ConnectionSettings s) => Path.Combine(Home, "cache",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{s.User}@{s.Host.ToLowerInvariant()}:{s.Port}/{s.RemoteFolder}")))[..16]);
}
