// ============================================================================
// TestCaseModels.cs
// Object model mirroring the JSON structure inside a .mtc file.
//
// A .mtc file is a 7-Zip (LZMA2) archive containing a "TestCase/" folder with
// one JSON file per bus:
//     [INFO]INFO.json, [CHAS]CHAS.json, [PT]PT.json   → send messages
//     [All]Read.json                                  → read/compare messages
//
// We model the *send* files here (that's what we fire). Each send file is a
// MtcBusFile; a whole .mtc is a MtcTestCase holding several MtcBusFile.
// ============================================================================
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CanTracer.Models;

/// <summary>One signal inside a message (matches JSON SignalItem entry).</summary>
public sealed class MtcSignal
{
    [JsonPropertyName("CanSignalName")] public string CanSignalName { get; set; } = "";
    [JsonPropertyName("Value")]         public string Value { get; set; } = "0";
    [JsonPropertyName("Start")]         public double Start { get; set; }
    [JsonPropertyName("Min")]           public double Min { get; set; }
    [JsonPropertyName("Max")]           public double Max { get; set; }
    [JsonPropertyName("Step")]          public double Step { get; set; }
    [JsonPropertyName("Loop")]          public int Loop { get; set; }
    [JsonPropertyName("Type")]          public int Type { get; set; } = 1;
    [JsonPropertyName("Extra")]         public object? Extra { get; set; }
}

/// <summary>One CAN message in a testcase (matches JSON Data entry).</summary>
public sealed class MtcMessage
{
    [JsonPropertyName("SignalItem")] public List<MtcSignal> SignalItem { get; set; } = new();
    [JsonPropertyName("ID")]         public string ID { get; set; } = "";
    [JsonPropertyName("Name")]       public string Name { get; set; } = "";
    [JsonPropertyName("Value")]      public string Value { get; set; } = "";
    [JsonPropertyName("Cycle_time")] public int CycleTime { get; set; }   // ms; 0 = one-shot
}

/// <summary>Contents of one [BUS]NAME.json send file.</summary>
public sealed class MtcBusFile
{
    [JsonPropertyName("Data")]               public List<MtcMessage> Data { get; set; } = new();
    [JsonPropertyName("Tool")]               public string Tool { get; set; } = "Mit";
    [JsonPropertyName("DBCType")]            public int DBCType { get; set; } = 1;
    [JsonPropertyName("DbcName")]            public string DbcName { get; set; } = "";
    [JsonPropertyName("EnableChecksum")]     public bool EnableChecksum { get; set; }
    [JsonPropertyName("EnableAliveCounter")] public bool EnableAliveCounter { get; set; }

    // Not serialized — the file name stem inside the archive, e.g. "[INFO]INFO".
    [JsonIgnore] public string EntryName { get; set; } = "";
    // Parsed bus tag from the entry name, e.g. "INFO".
    [JsonIgnore] public string BusTag { get; set; } = "";
}

/// <summary>A whole .mtc testcase: several send bus-files (+ optional read file).</summary>
public sealed class MtcTestCase
{
    public string FilePath { get; set; } = "";          // full path to the .mtc
    public string Name { get; set; } = "";              // file name without extension
    public List<MtcBusFile> BusFiles { get; set; } = new();
    public string? RawReadJson { get; set; }            // [All]Read.json kept verbatim

    public int TotalMessages
    {
        get { int n = 0; foreach (var b in BusFiles) n += b.Data.Count; return n; }
    }
}

/// <summary>
/// Lightweight view-model for one .mtc file shown in the sidebar browser.
/// Parsing happens lazily when the user opens/fires it.
/// </summary>
public sealed class TestCaseFile
{
    public string Path { get; }
    public string Name { get; }
    public string RelativeFolder { get; }

    public TestCaseFile(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);

        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var root = CanTracer.Services.TestCaseFolder.Root.TrimEnd('\\', '/');
        if (dir.StartsWith(root, System.StringComparison.OrdinalIgnoreCase))
            dir = dir.Substring(root.Length).TrimStart('\\', '/');
        RelativeFolder = string.IsNullOrEmpty(dir) ? "(root)" : dir;
    }
}
