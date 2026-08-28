// ============================================================================
// Sending.cs
// Cyclic transmit scheduler plus per-frame alive counter / CRC post-processing.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CanTracer.Models;

namespace CanTracer.Services
{
    public sealed class FrameProtectionState
    {
        public ulong AliveCounter { get; set; }

        // 1 = increase alive counter normally
        // 0 = set alive counter to invalid value
        // other = random/fault mode, matching CANoe-style behavior
        public byte AliveStatus { get; set; } = 1;

        // 1 = calculate real CRC
        // 0 = CRC = 0x00
        // other = CRC = 0xFF
        public byte CrcStatus { get; set; } = 1;
    }

    public sealed class FrameProtectionProfile
    {
        public bool EnableCrc { get; init; } = true;

        // CRC byte is at byte 0, so CRC calculation starts at byte 1.
        public int CrcStartIndex { get; init; } = 1;

        // Set to 8 if the message has SecOC at the end of the frame.
        // Set to 0 when the message has no SecOC.
        public int SecOcLength { get; init; } = 0;
    }

    public static class CanFramePostProcessor
    {
        private static readonly Random Random = new();

        public static void Apply(DbcMessage? dbcMsg, byte[] data, FrameProtectionState state)
        {
            if (dbcMsg == null || data.Length == 0) return;

            ApplyAliveCounter(dbcMsg, data, state);
            ApplyCrc(dbcMsg, data, state);
        }

        private static void ApplyAliveCounter(DbcMessage dbcMsg, byte[] data, FrameProtectionState state)
        {
            var sig = FindAliveSignal(dbcMsg);
            if (sig == null || sig.Length <= 0) return;

            var maxAliveValue = sig.Length >= 64
                ? ulong.MaxValue
                : (1UL << sig.Length) - 1;

            var invalidAliveValue = maxAliveValue;

            ulong value;

            if (state.AliveStatus == 1)
            {
                value = state.AliveCounter;

                state.AliveCounter = state.AliveCounter >= maxAliveValue
                    ? 0
                    : state.AliveCounter + 1;
            }
            else if (state.AliveStatus == 0)
            {
                value = invalidAliveValue;
                state.AliveCounter = value;
            }
            else
            {
                value = (ulong)(3 * Random.Next(4));

                if (value > maxAliveValue)
                    value = 0;

                state.AliveCounter = value;
            }

            sig.Encode(value, data);
        }

        private static void ApplyCrc(DbcMessage dbcMsg, byte[] data, FrameProtectionState state)
        {
            var profile = CanFrameProtectionProfiles.GetProfile(dbcMsg);
            if (profile == null || !profile.EnableCrc) return;

            var sig = FindChecksumSignal(dbcMsg);
            if (sig == null) return;

            int dlc = Math.Min(data.Length, Math.Max(0, dbcMsg.Dlc));
            if (dlc <= 0) return;

            byte crc;

            if (state.CrcStatus == 1)
            {
                sig.Encode(0, data);

                int len = dlc - profile.SecOcLength;

                if (len <= profile.CrcStartIndex)
                    return;

                crc = Crc8SaeJ1850(
                    data,
                    startIndex: profile.CrcStartIndex,
                    len: len);
            }
            else if (state.CrcStatus == 0)
            {
                crc = 0x00;
            }
            else
            {
                crc = 0xFF;
            }

            sig.Encode(crc, data);
        }

        private static byte Crc8SaeJ1850(byte[] data, int startIndex, int len)
        {
            byte crc = 0xFF;
            const byte poly = 0x1D;

            for (int i = startIndex; i < len; i++)
            {
                crc ^= data[i];

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x80) != 0
                        ? (byte)((crc << 1) ^ poly)
                        : (byte)(crc << 1);
                }
            }

            return (byte)(crc ^ 0xFF);
        }

        private static DbcSignal? FindAliveSignal(DbcMessage msg)
        {
            var expected = Normalize("ALV_" + msg.Name);

            return msg.Signals.FirstOrDefault(s => Normalize(s.Name) == expected)
                ?? msg.Signals.FirstOrDefault(s =>
                {
                    var n = Normalize(s.Name);

                    return n.Contains("alive")
                           || n.Contains("rollingcounter")
                           || n.Contains("messagecounter")
                           || n.Contains("msgcounter")
                           || n.EndsWith("counter")
                           || n.EndsWith("cnt")
                           || n.StartsWith("alv");
                });
        }

        private static DbcSignal? FindChecksumSignal(DbcMessage msg)
            => msg.Signals.FirstOrDefault(s =>
            {
                var n = Normalize(s.Name);

                return n.Contains("checksum")
                       || n.Contains("chksum")
                       || n.Contains("crc")
                       || n.Contains("chksm");
            });

        private static string Normalize(string value)
            => new(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }

    public static class CanFrameProtectionProfiles
    {
        private static readonly Dictionary<uint, FrameProtectionProfile> ProfilesById = new()
        {
        };

        public static FrameProtectionProfile? GetProfile(DbcMessage msg)
            => ProfilesById.TryGetValue(msg.Id, out var profile)
                ? profile
                : null;
    }
}

namespace CanTracer.ViewModels
{
    using CanTracer.Services;

    public sealed class CyclicSender : IDisposable
    {
        public sealed class Job
        {
            public Guid     Id           { get; init; } = Guid.NewGuid();
            public CanBus   Bus          { get; init; } = null!;
            public uint     CanId        { get; init; }
            public byte[]   Data         { get; init; } = Array.Empty<byte>();
            public bool     IsExtended   { get; init; }
            public bool     IsFd         { get; init; }
            public bool     Brs          { get; init; }
            public int      PeriodMs     { get; init; }
            public string   MessageName  { get; init; } = "";
            public DbcMessage? DbcMessage { get; init; }
            public FrameProtectionState Protection { get; } = new();

            internal Timer? Timer { get; set; }
            public  int SentCount;
        }

        private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
        private readonly BusManager _busMgr;

        public CyclicSender(BusManager busMgr) { _busMgr = busMgr; }

        /// <summary>Live view of active jobs (snapshot). For UI binding, prefer
        /// listening to JobsChanged.</summary>
        public IReadOnlyCollection<Job> Jobs => _jobs.Values.ToList();

        public event Action? JobsChanged;

        public Job Start(CanBus bus, uint id, byte[] data, bool extended, bool fd, bool brs,
                         int periodMs, string messageName, DbcMessage? dbcMessage = null)
        {
            if (periodMs < 10) periodMs = 10;
            var job = new Job
            {
                Bus = bus, CanId = id, Data = data,
                IsExtended = extended, IsFd = fd, Brs = brs,
                PeriodMs = periodMs,
                MessageName = messageName,
                DbcMessage = dbcMessage
            };
            job.Timer = new Timer(_ => Tick(job), null, 0, periodMs);
            _jobs[job.Id] = job;
            JobsChanged?.Invoke();
            return job;
        }

        public void Stop(Guid jobId)
        {
            if (_jobs.TryRemove(jobId, out var job))
            {
                job.Timer?.Dispose();
                JobsChanged?.Invoke();
            }
        }

        public void StopAll()
        {
            foreach (var j in _jobs.Values) j.Timer?.Dispose();
            _jobs.Clear();
            JobsChanged?.Invoke();
        }

        private void Tick(Job job)
        {
            if (!job.Bus.IsConnected) return;
            var data = job.Data.ToArray();
            CanFramePostProcessor.Apply(job.DbcMessage, data, job.Protection);

            var ok = _busMgr.Send(job.Bus, job.CanId, data, job.IsExtended, job.IsFd, job.Brs);
            if (ok) Interlocked.Increment(ref job.SentCount);
        }

        public void Dispose() => StopAll();
    }
}
