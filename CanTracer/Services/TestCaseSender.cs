// ============================================================================
// TestCaseSender.cs
// Fires a whole .mtc testcase onto the configured buses.
//
// Mapping rule (per user's spec): a send file named "[ICAN]ICAN.json" targets
// the bus whose Name == "ICAN". A testcase with several bus-files therefore
// fires on several CAN lines simultaneously.
//
// Each message is sent cyclically at its Cycle_time (ms). Cycle_time == 0 means
// fire once. Signal values come from the SignalItem list, encoded via the
// target bus's DBC.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using CanTracer.Models;

namespace CanTracer.Services;

public sealed class TestCaseSender : IDisposable
{
    // One scheduled cyclic message.
    private sealed class Job
    {
        public CanBus Bus = null!;
        public uint Id;
        public byte[] Data = Array.Empty<byte>();
        public bool Extended;
        public DbcMessage? DbcMessage;
        public FrameProtectionState Protection = new();
        public int CycleMs;
        public long NextDueMs;
        public string MessageName = "";
        public string BusTag = "";
    }

    private readonly List<Job> _jobs = new();
    private Timer? _timer;
    private readonly object _lock = new();
    private long _elapsedMs;
    private BusManager _busMgr = null!;

    public bool IsRunning { get; private set; }

    private static readonly Dictionary<string, string> TagAlias =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["INFO"]  = "ICAN",
        ["CHAS"]  = "CCAN",
        ["PT"]    = "PCAN",
        ["BODY"]  = "BCAN",
        ["LOCAL"] = "LCAN",
        ["SAFE"]  = "SCAN",
        ["BACK"]  = "BACAN",
        ["REEV"]  = "REEVCAN",
    };

    /// <summary>Raised for each per-message result while building the plan (for UI log).</summary>
    public event Action<string>? Report;

    /// <summary>
    /// Build the firing plan from a testcase, resolving each bus-file tag to a
    /// configured CanBus and encoding each message via that bus's DBC.
    /// Returns the number of messages successfully scheduled.
    /// </summary>
    public int Prepare(MtcTestCase tc, IReadOnlyList<CanBus> buses, BusManager busMgr)
    {
        lock (_lock)
        {
            _busMgr = busMgr;
            _jobs.Clear();
            int ok = 0, skipped = 0;

            foreach (var busFile in tc.BusFiles)
            {
                // Match the file's tag to a configured bus (exact, then alias).
                var bus = ResolveBus(busFile.BusTag, buses);
                if (bus == null)
                {
                    Report?.Invoke($"⚠ No bus matches tag '{busFile.BusTag}' — skipping {busFile.Data.Count} message(s)");
                    skipped += busFile.Data.Count;
                    continue;
                }

                foreach (var msg in busFile.Data)
                {
                    if (!TryBuildJob(bus, busFile.BusTag, msg, out var job))
                    {
                        skipped++;
                        continue;
                    }
                    _jobs.Add(job);
                    ok++;
                }
            }

            Report?.Invoke($"Prepared {ok} message(s)" + (skipped > 0 ? $", skipped {skipped}" : ""));
            return ok;
        }
    }

    /// <summary>Resolve a testcase tag to a configured bus: exact name, then alias.</summary>
    public static CanBus? ResolveBus(string tag, IReadOnlyList<CanBus> buses)
    {
        // 1. Exact match on bus name.
        var bus = buses.FirstOrDefault(b =>
            string.Equals(b.Name, tag, StringComparison.OrdinalIgnoreCase));
        if (bus != null) return bus;

        // 2. Alias table (INFO→ICAN, CHAS→CCAN, PT→PCAN, ...).
        if (TagAlias.TryGetValue(tag, out var aliasName))
        {
            bus = buses.FirstOrDefault(b =>
                string.Equals(b.Name, aliasName, StringComparison.OrdinalIgnoreCase));
            if (bus != null) return bus;
        }
        return null;
    }

    private bool TryBuildJob(CanBus bus, string tag, MtcMessage msg, out Job job)
    {
        job = null!;

        // Parse the message ID ("0x085" or "133").
        if (!TryParseId(msg.ID, out var id, out var extended))
        {
            Report?.Invoke($"⚠ {tag}/{msg.Name}: bad ID '{msg.ID}'");
            return false;
        }

        // Look up the DBC message for encoding. Prefer matching by ID; fall back to name.
        DbcMessage? dbcMsg = null;
        if (bus.Dbc.TryGetValue(id, out var byId)) dbcMsg = byId;
        else dbcMsg = bus.Dbc.Values.FirstOrDefault(m =>
                 string.Equals(m.Name, msg.Name, StringComparison.OrdinalIgnoreCase));

        byte[] data;
        if (dbcMsg != null)
        {
            // Build a name→value map from the testcase signals.
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in msg.SignalItem)
                if (TryParseValue(s.Value, out var v))
                    values[s.CanSignalName] = v;
            data = dbcMsg.EncodeMessage(values);
        }
        else
        {
            // No DBC match — send all-zero payload of length 8 as a fallback.
            Report?.Invoke($"⚠ {tag}/{msg.Name} (0x{id:X}): not in DBC, sending zeros");
            data = new byte[8];
        }

        job = new Job
        {
            Bus = bus,
            Id = id,
            Data = data,
            Extended = extended,
            DbcMessage = dbcMsg,
            CycleMs = Math.Max(0, msg.CycleTime),
            NextDueMs = 0,           // fire immediately on first tick
            MessageName = msg.Name,
            BusTag = tag,
        };
        return true;
    }

    /// <summary>Start firing. The tick resolution is 1 ms.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning || _jobs.Count == 0) return;
            _elapsedMs = 0;
            IsRunning = true;
            _timer = new Timer(Tick, null, 0, 1);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            IsRunning = false;
        }
    }

    private void Tick(object? state)
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            _elapsedMs++;

            foreach (var job in _jobs)
            {
                if (_elapsedMs < job.NextDueMs) continue;

                SendOne(job);

                if (job.CycleMs <= 0)
                    job.NextDueMs = long.MaxValue;       // one-shot: never again
                else
                    job.NextDueMs = _elapsedMs + job.CycleMs;
            }

            // If every job is one-shot and done, auto-stop.
            if (_jobs.All(j => j.NextDueMs == long.MaxValue))
                IsRunning = false;
        }
    }

    private void SendOne(Job job)
    {
        try
        {
            var data = job.Data.ToArray();
            CanFramePostProcessor.Apply(job.DbcMessage, data, job.Protection);

            _busMgr.Send(job.Bus, job.Id, data, job.Extended,
             fd: job.Bus.IsFd, brs: job.Bus.IsFd);
        }
        catch { /* a single send failure shouldn't kill the whole testcase */ }
    }

    // ----- parsing helpers ----------------------------------------------------

    /// <summary>Public ID parser (hex "0x085" or decimal) used by the VM too.</summary>
    public static bool TryParseIdStatic(string text, out uint id)
        => TryParseId(text, out id, out _);

    private static bool TryParseId(string text, out uint id, out bool extended)
    {
        id = 0; extended = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var body = hex ? text.Substring(2) : text;
        var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!uint.TryParse(body, style, CultureInfo.InvariantCulture, out id)) return false;
        extended = id > 0x7FF;     // 11-bit max for standard frames
        return true;
    }

    private static bool TryParseValue(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
        { value = hx; return true; }
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose() => Stop();
}
