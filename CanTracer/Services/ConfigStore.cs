// ============================================================================
// ConfigStore.cs
// Persists the bus configuration to "config.json" next to the executable so
// the user doesn't have to re-add buses / re-pick DBC files every launch.
//
// We persist only the *configuration* (name, channel, DBC path, FD flags) —
// not runtime state (connected, frame counts). On startup MainViewModel loads
// this and recreates the buses; DBCs are re-parsed from their stored paths.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CanTracer.Services;

/// <summary>One persisted bus entry. Mirrors the editable fields of CanBus.</summary>
public sealed class BusConfig
{
    public string Name         { get; set; } = "";
    public string ChannelId    { get; set; } = "";
    public string ChannelLabel { get; set; } = "";
    public bool   IsFd         { get; set; }
    public string Baud         { get; set; } = "500 kbit/s";
    public string FdNominal    { get; set; } = "500";
    public string FdData       { get; set; } = "2000";
    public string DbcPath      { get; set; } = "";
    public string ColorHex     { get; set; } = "#FF808080";
}

/// <summary>Root config object serialized to config.json.</summary>
public sealed class AppConfig
{
    public List<BusConfig> Buses { get; set; } = new();
}

public static class ConfigStore
{
    private static string ConfigPath
        => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    /// <summary>Load config.json. Returns empty config if missing or corrupt.</summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            // Corrupt file → start fresh rather than crashing.
            return new AppConfig();
        }
    }

    /// <summary>Write config.json (atomic: write temp then replace).</summary>
    public static void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOpts);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            File.Move(tmp, ConfigPath);
        }
        catch
        {
            // Saving config is best-effort; never crash the app over it.
        }
    }
}
