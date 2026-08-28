// ============================================================================
// MtcStore.cs
// Reads and writes .mtc testcase files.
//
// A .mtc is a 7-Zip (LZMA2) archive with this internal layout:
//     TestCase/[INFO]INFO.json
//     TestCase/[CHAS]CHAS.json
//     TestCase/[PT]PT.json
//     TestCase/[All]Read.json
//
// We use SharpCompress (managed, no native dll) to read/write 7z.
// The send files are deserialized into MtcBusFile; the read file is kept as
// raw JSON so we round-trip it unchanged.
//
// Requires NuGet package: SharpCompress
// ============================================================================
using CanTracer.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CanTracer.Services;

public static class MtcStore
{
    private const string FolderPrefix = "TestCase/";
    private const string ReadEntryName = "[All]Read.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,   // the original tool writes compact JSON
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    // ----- read ---------------------------------------------------------------

    /// <summary>Open and parse a .mtc file into a MtcTestCase.</summary>
    public static MtcTestCase Load(string path)
    {
        var tc = new MtcTestCase
        {
            FilePath = path,
            Name = Path.GetFileNameWithoutExtension(path),
        };

        using var archive = SevenZipArchive.OpenArchive(path);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            // entry.Key looks like "TestCase/[INFO]INFO.json"
            var key = entry.Key?.Replace('\\', '/') ?? "";
            var fileName = Path.GetFileName(key);
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            using var ms = new MemoryStream();
            using (var es = entry.OpenEntryStream()) es.CopyTo(ms);
            var json = Encoding.UTF8.GetString(ms.ToArray());

            if (fileName.Equals(ReadEntryName, StringComparison.OrdinalIgnoreCase))
            {
                tc.RawReadJson = json;   // keep verbatim for round-trip
                continue;
            }

            // Send file: parse into MtcBusFile.
            try
            {
                var busFile = JsonSerializer.Deserialize<MtcBusFile>(json, JsonOpts);
                if (busFile != null)
                {
                    var stem = Path.GetFileNameWithoutExtension(fileName);   // "[INFO]INFO"
                    busFile.EntryName = stem;
                    busFile.BusTag = ParseBusTag(stem);
                    tc.BusFiles.Add(busFile);
                }
            }
            catch
            {
                // Skip a malformed send file rather than failing the whole load.
            }
        }

        tc.BusFiles = tc.BusFiles.OrderBy(b => b.BusTag).ToList();
        return tc;
    }

    /// <summary>Extract the "INFO" from "[INFO]INFO".</summary>
    private static string ParseBusTag(string stem)
    {
        var open = stem.IndexOf('[');
        var close = stem.IndexOf(']');
        if (open == 0 && close > 1) return stem.Substring(1, close - 1);
        return stem;
    }

    // ----- write --------------------------------------------------------------

    /// <summary>Write a MtcTestCase back to a .mtc (7z/LZMA2) archive.</summary>
    public static void Save(MtcTestCase tc, string path)
    {
        // SharpCompress ignores directory entries when writing 7z, so we add
        // each entry with an explicit "TestCase/..." key and keep the byte
        // streams alive until SaveTo completes (it reads them lazily).
        var streams = new List<MemoryStream>();
        try
        {
            using var archive = ArchiveFactory.CreateArchive<SevenZipWriterOptions>();

            foreach (var bf in tc.BusFiles)
            {
                var name = string.IsNullOrEmpty(bf.EntryName)
                    ? $"[{bf.BusTag}]{bf.BusTag}"
                    : bf.EntryName;
                var json = JsonSerializer.Serialize(bf, JsonOpts);
                var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(json));
                streams.Add(ms);
                archive.AddEntry(FolderPrefix + name + ".json", ms, closeStream: false, ms.Length);
            }

            if (!string.IsNullOrEmpty(tc.RawReadJson))
            {
                var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(tc.RawReadJson));
                streams.Add(ms);
                archive.AddEntry(FolderPrefix + ReadEntryName, ms, closeStream: false, ms.Length);
            }

            if (File.Exists(path)) File.Delete(path);
            using var outStream = File.Create(path);
            // The original .mtc files use LZMA2; match that for compatibility.
            archive.SaveTo(outStream, new SevenZipWriterOptions(CompressionType.LZMA2));
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    // ----- enumerate ----------------------------------------------------------

    /// <summary>List all .mtc files under a folder (recursive), as relative info.</summary>
    public static List<string> EnumerateFiles(string rootFolder)
    {
        if (!Directory.Exists(rootFolder)) return new List<string>();
        return Directory.EnumerateFiles(rootFolder, "*.mtc", SearchOption.AllDirectories)
                        .OrderBy(p => p)
                        .ToList();
    }
}

public static class TestCaseFolder
{
    /// <summary>Absolute path to the TestCases/ folder (created on first access).</summary>
    public static string Root
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "TestCases");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>Open the TestCases/ folder in Windows Explorer.</summary>
    public static void OpenRoot()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Root,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
