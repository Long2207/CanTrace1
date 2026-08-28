// ============================================================================
// DbcParser.cs — DBC parser (BO_ + SG_ + VAL_).
// Multiplexed signals (M, m0, m1...) are intentionally ignored.
//
// VAL_ lines give value descriptions (enums), e.g.:
//     VAL_ 1235 BMS_Warning_HVILSts 0 "Normal" 1 "Fault" 2 "Unknown" ;
// We attach these to the matching DbcSignal so the UI can show "1 (Fault)"
// instead of a bare "1".
// ============================================================================
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CanTracer.Models;

namespace CanTracer.Services;

public static class DbcParser
{
    private static readonly Regex Bo = new(
        @"^BO_\s+(?<id>\d+)\s+(?<name>\w+)\s*:\s*(?<dlc>\d+)\s*(?<tx>\w+)?",
        RegexOptions.Compiled);

    private static readonly Regex Sg = new(
        @"^\s*SG_\s+(?<name>\w+)\s*(?:M|m\d+)?\s*:\s*" +
        @"(?<start>\d+)\|(?<length>\d+)@(?<order>[01])(?<sign>[+-])\s*" +
        @"\((?<factor>[-+0-9.eE]+),(?<offset>[-+0-9.eE]+)\)\s*" +
        @"\[(?<min>[-+0-9.eE]+)\|(?<max>[-+0-9.eE]+)\]\s*""(?<unit>[^""]*)""",
        RegexOptions.Compiled);

    // VAL_ <msgId> <signalName> (<int> "<desc>")+ ;
    private static readonly Regex Val = new(
        @"^VAL_\s+(?<id>\d+)\s+(?<sig>\w+)\s+(?<pairs>.*?)\s*;",
        RegexOptions.Compiled);

    // matches:  <number> "<text>"
    private static readonly Regex ValPair = new(
        @"(?<val>-?\d+)\s+""(?<desc>[^""]*)""",
        RegexOptions.Compiled);

    public static Dictionary<uint, DbcMessage> Parse(string path)
    {
        var result = new Dictionary<uint, DbcMessage>();
        DbcMessage? current = null;

        // First pass: messages + signals.
        var allLines = File.ReadAllLines(path);
        foreach (var raw in allLines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { current = null; continue; }

            var bo = Bo.Match(line);
            if (bo.Success)
            {
                var id = uint.Parse(bo.Groups["id"].Value, CultureInfo.InvariantCulture) & 0x7FFFFFFF;
                current = new DbcMessage
                {
                    Id = id,
                    Name = bo.Groups["name"].Value,
                    Dlc = int.Parse(bo.Groups["dlc"].Value, CultureInfo.InvariantCulture),
                    Transmitter = bo.Groups["tx"].Success ? bo.Groups["tx"].Value : ""
                };
                result[id] = current;
                continue;
            }

            if (current is null) continue;

            var sg = Sg.Match(line);
            if (sg.Success)
            {
                current.Signals.Add(new DbcSignal
                {
                    Name         = sg.Groups["name"].Value,
                    StartBit     = int.Parse(sg.Groups["start"].Value, CultureInfo.InvariantCulture),
                    Length       = int.Parse(sg.Groups["length"].Value, CultureInfo.InvariantCulture),
                    LittleEndian = sg.Groups["order"].Value == "1",
                    Signed       = sg.Groups["sign"].Value == "-",
                    Factor       = double.Parse(sg.Groups["factor"].Value, CultureInfo.InvariantCulture),
                    Offset       = double.Parse(sg.Groups["offset"].Value, CultureInfo.InvariantCulture),
                    Min          = double.Parse(sg.Groups["min"].Value, CultureInfo.InvariantCulture),
                    Max          = double.Parse(sg.Groups["max"].Value, CultureInfo.InvariantCulture),
                    Unit         = sg.Groups["unit"].Value
                });
            }
        }

        // Second pass: VAL_ value tables (they appear after all BO_/SG_).
        foreach (var raw in allLines)
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith("VAL_")) continue;
            var v = Val.Match(line);
            if (!v.Success) continue;

            var id = uint.Parse(v.Groups["id"].Value, CultureInfo.InvariantCulture) & 0x7FFFFFFF;
            if (!result.TryGetValue(id, out var msg)) continue;

            var sigName = v.Groups["sig"].Value;
            var signal = msg.Signals.Find(s => s.Name == sigName);
            if (signal == null) continue;

            foreach (Match pair in ValPair.Matches(v.Groups["pairs"].Value))
            {
                var num = long.Parse(pair.Groups["val"].Value, CultureInfo.InvariantCulture);
                signal.ValueTable[num] = pair.Groups["desc"].Value;
            }
        }

        return result;
    }
}
