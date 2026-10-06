#nullable enable annotations
// Test fixture: verbatim PacedSendQueue from src/MPTransport.cs at d1e5d663a47df893541abd9147f6f6b3d5003cbc.
using System;
using System.Collections.Generic;
namespace BigAmbitionsMP {
    internal sealed class PacedSendQueue
    {
        /// <summary>Release the next paced chunk only while the link's own backlog is
        /// UNDER this.  256KB ≈ one second of a 250-330KB/s relay link (the rates
        /// measured in field 20260818-215459), so an urgent message enqueued at the
        /// worst moment waits about a second instead of the whole convoy.
        /// TUNABLE: the `outQ=` figures on the round-276 phase lines are the field
        /// fingerprint to tune against — if outQ is routinely pinned at the headroom
        /// while mirrors crawl, the link is slower than this assumes and the number
        /// should come DOWN (latency), not up (throughput).</summary>
        public const long HeadroomBytes = 256L * 1024;

        /// <summary>A payload waiting longer than this without finishing gets one WARN
        /// naming the backlog — silence during a 60s stall is undiagnosable.</summary>
        private const long SlowWarnSeconds = 60;

        private sealed class Payload
        {
            public byte[][] Chunks = Array.Empty<byte[]>();
            public int Next;                 // index of the next chunk to release
            public long TotalBytes;
            public long UnsentBytes;
            public string Key = "";
            public long QueuedTicks;
            public bool Warned;
            /// <summary>Partially sent — a supersede must NEVER drop one of these: its
            /// first chunks are already on the wire and the receiver would wait out the
            /// 120s reassembly timeout on a message that can never complete.</summary>
            public bool Started => Next > 0;
        }

        private readonly LinkedList<Payload> _q = new();
        private long _bytes;
        private readonly string _tag;

        public PacedSendQueue(string tag) { _tag = tag; }

        /// <summary>Bytes accepted for this peer that have not been released yet.</summary>
        public long Bytes { get { lock (_q) return _bytes; } }

        /// <summary>Queue a pre-chunked payload.  When supersedeKey is non-empty, any
        /// queued payload with the same key that has NOT started sending is dropped:
        /// a store mirror for the same (session, character) is a full replacement.
        /// Round-282c honesty note (rig-measured): because the autosave ROTATION renames
        /// the session every save, consecutive sweeps rarely share member keys — in
        /// practice supersede mostly catches the small manifest piece (~0.07% of bytes
        /// in the soak).  It is kept for semantic correctness (same-key = same file on
        /// the receiver), not as a convoy collapser.  Started payloads are never touched
        /// (see Payload.Started).</summary>
        public void Enqueue(byte[][] chunks, string supersedeKey, string describe)
        {
            if (chunks == null || chunks.Length == 0) return;
            long total = 0;
            foreach (var c in chunks) total += c?.Length ?? 0;
            var p = new Payload
            {
                Chunks = chunks, TotalBytes = total, UnsentBytes = total,
                Key = supersedeKey ?? "", QueuedTicks = DateTime.UtcNow.Ticks,
            };
            long droppedBytes = 0; int droppedCount = 0;
            lock (_q)
            {
                if (p.Key.Length > 0)
                {
                    var node = _q.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        if (!node.Value.Started && node.Value.Key == p.Key)
                        {
                            droppedBytes += node.Value.UnsentBytes; droppedCount++;
                            _bytes -= node.Value.UnsentBytes;
                            _q.Remove(node);
                        }
                        node = next;
                    }
                }
                _q.AddLast(p);
                _bytes += total;
            }
            if (droppedCount > 0)
                Plugin.Logger.LogInfo($"[{_tag}] paced mirror '{p.Key}' to {describe}: {droppedCount} queued copy/copies "
                                    + $"({droppedBytes / 1024}KB, none sent yet) superseded by a fresher mirror.");
            Plugin.Logger.LogInfo($"[{_tag}] paced mirror: {total / 1024}KB in {chunks.Length} chunk(s) to {describe}.");
        }

        /// <summary>Pump tick: the next chunk to hand to the normal send path, or null
        /// when the queue is empty or the link is still too backed up.  linkBacklog is
        /// the transport's OWN backlog — see MPLink.TransportPendingBytes for why it
        /// must not include this queue.</summary>
        public byte[]? TryRelease(long linkBacklog, string describe)
        {
            // Round-282c (verifier nit): Bytes is read from the main thread (phase probe,
            // quit drain) — logging inside the lock let a slow logger stall those reads.
            // Messages are composed under the lock and emitted after it releases.
            List<string>? warns = null;
            string? drained = null;
            byte[]? released = null;
            lock (_q)
            {
                if (_q.Count == 0) return null;
                long now = DateTime.UtcNow.Ticks;

                // Stall warning fires whether or not the gate opens — a payload stuck
                // behind a congested link is exactly the case worth naming.
                for (var n = _q.First; n != null; n = n.Next)
                {
                    var w = n.Value;
                    if (w.Warned || now - w.QueuedTicks < TimeSpan.TicksPerSecond * SlowWarnSeconds) continue;
                    w.Warned = true;
                    (warns ??= new List<string>()).Add($"[{_tag}] paced mirror to {describe} still unsent after "
                        + $"{(now - w.QueuedTicks) / TimeSpan.TicksPerSecond}s: {w.UnsentBytes / 1024}KB of it left, "
                        + $"{_bytes / 1024}KB paced behind it, link backlog {linkBacklog / 1024}KB.");
                }

                if (linkBacklog < HeadroomBytes)
                {
                    var head = _q.First!.Value;
                    released = head.Chunks[head.Next++];
                    int len = released?.Length ?? 0;
                    head.UnsentBytes -= len; _bytes -= len;
                    if (head.Next >= head.Chunks.Length)
                    {
                        _q.RemoveFirst();
                        double secs = (now - head.QueuedTicks) / (double)TimeSpan.TicksPerSecond;
                        // "drained" = every chunk has been handed to the link, NOT acked by
                        // the peer (no transport here reports acks).  Worded so a reader of
                        // the log cannot mistake it for delivery confirmation.
                        drained = $"[{_tag}] paced mirror drained to {describe} in {secs:F1}s "
                                + $"({head.TotalBytes / 1024}KB released to the link).";
                    }
                }
            }
            try
            {
                if (warns != null) foreach (var w in warns) Plugin.Logger.LogWarning(w);
                if (drained != null) Plugin.Logger.LogInfo(drained);
            }
            catch { }
            return released;
        }

        /// <summary>Link is gone — nothing queued here can ever be delivered.</summary>
        public void Clear()
        {
            lock (_q) { if (_q.Count == 0) return; _q.Clear(); _bytes = 0; }
        }
    }
}
