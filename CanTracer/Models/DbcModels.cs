// ============================================================================
// DbcModels.cs
// Parsed DBC message/signal models plus payload encode/decode helpers.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CanTracer.Models;

public class DbcMessage
{
    public uint   Id          { get; set; }
    public string Name        { get; set; } = "";
    public int    Dlc         { get; set; }
    /// <summary>Transmitter node from the BO_ line (e.g. "XGW", "ACM").</summary>
    public string Transmitter { get; set; } = "";
    public List<DbcSignal> Signals { get; } = new();

    /// <summary>Encode a payload from a signal-name -> physical-value map.</summary>
    public byte[] EncodeMessage(IReadOnlyDictionary<string, double> values)
    {
        var data = new byte[Math.Max(Dlc, 8)];
        foreach (var sig in Signals)
            if (values.TryGetValue(sig.Name, out var v))
                sig.Encode(v, data);
        return data;
    }
}

public class DbcSignal
{
    public string Name         { get; set; } = "";
    public int    StartBit     { get; set; }
    public int    Length       { get; set; }
    /// <summary>true = little-endian (Intel), false = big-endian (Motorola).</summary>
    public bool   LittleEndian { get; set; } = true;
    public bool   Signed       { get; set; }
    public double Factor       { get; set; } = 1.0;
    public double Offset       { get; set; }
    public double Min          { get; set; }
    public double Max          { get; set; }
    public string Unit         { get; set; } = "";

    /// <summary>Value descriptions from VAL_ (raw value -> label). Empty if none.</summary>
    public Dictionary<long, string> ValueTable { get; } = new();
    public bool HasValueTable => ValueTable.Count > 0;

    /// <summary>Decode raw payload bytes into a physical value.</summary>
    public double Decode(byte[] data)
    {
        if (data is null || data.Length == 0) return 0.0;

        ulong raw = 0;
        if (LittleEndian)
        {
            for (int i = 0; i < Length; i++)
            {
                int bitPos = StartBit + i;
                int byteIdx = bitPos / 8;
                int bitInByte = bitPos % 8;
                if (byteIdx < data.Length && ((data[byteIdx] >> bitInByte) & 1) == 1)
                    raw |= 1UL << i;
            }
        }
        else
        {
            int bitPos = StartBit;
            for (int i = 0; i < Length; i++)
            {
                int byteIdx = bitPos / 8;
                int bitInByte = bitPos % 8;
                if (byteIdx < data.Length && ((data[byteIdx] >> bitInByte) & 1) == 1)
                    raw |= 1UL << (Length - 1 - i);
                if (bitInByte == 0) bitPos += 15; else bitPos -= 1;
            }
        }

        if (Signed)
        {
            long signed = (long)raw;
            if ((raw & (1UL << (Length - 1))) != 0)
                signed |= ~((1L << Length) - 1);
            return signed * Factor + Offset;
        }
        return raw * Factor + Offset;
    }

    /// <summary>Encode a physical value into the payload bytes (in place).</summary>
    public void Encode(double physValue, byte[] data)
    {
        var phys = physValue;
        if (!(Min == 0 && Max == 0))
            phys = Math.Max(Min, Math.Min(Max, phys));
        var rawDouble = (phys - Offset) / Factor;

        ulong raw;
        var mask = (Length >= 64) ? ~0UL : (1UL << Length) - 1;
        if (Signed)
        {
            long s = (long)Math.Round(rawDouble);
            raw = (ulong)s & mask;
        }
        else
        {
            var u = (long)Math.Round(rawDouble);
            if (u < 0) u = 0;
            raw = (ulong)u & mask;
        }

        if (LittleEndian)
        {
            for (int i = 0; i < Length; i++)
            {
                int bitPos = StartBit + i;
                int byteIdx = bitPos / 8;
                int bitInByte = bitPos % 8;
                if (byteIdx >= data.Length) break;
                if (((raw >> i) & 1) == 1) data[byteIdx] |= (byte)(1 << bitInByte);
                else                        data[byteIdx] &= (byte)~(1 << bitInByte);
            }
        }
        else
        {
            int bitPos = StartBit;
            for (int i = 0; i < Length; i++)
            {
                int byteIdx = bitPos / 8;
                int bitInByte = bitPos % 8;
                if (byteIdx >= data.Length) break;
                var bit = (raw >> (Length - 1 - i)) & 1;
                if (bit == 1) data[byteIdx] |= (byte)(1 << bitInByte);
                else          data[byteIdx] &= (byte)~(1 << bitInByte);
                if (bitInByte == 0) bitPos += 15; else bitPos -= 1;
            }
        }
    }

    /// <summary>Format a value with its label, e.g. "1: Fault"; else number (+unit).</summary>
    public string FormatValue(double physValue)
    {
        var key = (long)Math.Round(physValue);
        if (ValueTable.TryGetValue(key, out var desc))
            return $"{key}: {desc}";
        var num = physValue.ToString("0.###", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(Unit) ? num : $"{num} {Unit}";
    }

    /// <summary>Dropdown choices ("0: Off", "1: On", ...) - only if a table exists.</summary>
    public IEnumerable<string> ValueChoices()
    {
        foreach (var kv in ValueTable)
            yield return $"{kv.Key}: {kv.Value}";
    }
}
