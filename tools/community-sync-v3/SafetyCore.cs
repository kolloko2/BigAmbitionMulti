using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BAMP.SyncFix
{
    public static class SafetyCore
    {
        public const int MaxMessage = 128 * 1024 * 1024;
        public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public static int InitialCapacity(int expanded) => Math.Min(Math.Max(expanded, 0), 65536);
        public static List<T> RemainingAfter<T>(IEnumerable<T> original,int completed,Func<T,bool> valid) where T:class
        {
            var result=new List<T>();
            foreach(var item in original)
            {if(item==null)continue;if(completed>0&&valid(item)){completed--;continue;}result.Add(item);}
            return result;
        }
        public static void ValidateLength(byte[] data, int expected)
        {
            if (data == null || data.Length == 0 || data.Length > MaxMessage || expected <= 0 || data.Length != expected)
                throw new InvalidDataException("Save length does not match its declared length; existing save retained.");
        }
        public static void CopyBounded(Stream source, Stream target)
        {
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxMessage) throw new InvalidDataException("Decoded network message exceeds 128 MiB.");
                target.Write(buffer, 0, read);
            }
        }
        public static void AtomicBytes(string path, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            string full = Path.GetFullPath(path), temp = full + ".bamp-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(data, 0, data.Length); stream.Flush(true); }
                if (File.Exists(full)) File.Replace(temp, full, full + ".bamp-backup", true);
                else File.Move(temp, full);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void AtomicText(string path, string text) => AtomicBytes(path, new UTF8Encoding(false).GetBytes(text ?? ""));
        private static readonly object PairLock = new object();
        public static void RecoverPair(string path)
        {
            lock (PairLock)
            {
                string full=Path.GetFullPath(path), marker=full+".bamp-pair";
                if(!File.Exists(marker))return;
                string flags=File.ReadAllText(marker);
                if(flags!="00"&&flags!="01"&&flags!="10"&&flags!="11")throw new InvalidDataException("Invalid save-pair recovery marker.");
                if(flags[0]=='1')AtomicBytes(full,File.ReadAllBytes(full+".bamp-pair-old"));
                else if(File.Exists(full))File.Delete(full);
                if(flags[1]=='1')AtomicBytes(full+".meta",File.ReadAllBytes(full+".meta.bamp-pair-old"));
                else if(File.Exists(full+".meta"))File.Delete(full+".meta");
                File.Delete(marker);
            }
        }
        public static void AtomicSavePair(string path, byte[] data, string meta)
        {
            if(string.IsNullOrEmpty(meta)){AtomicBytes(path,data);return;}
            lock(PairLock)
            {
                string full=Path.GetFullPath(path);RecoverPair(full);
                bool oldHsg=File.Exists(full), oldMeta=File.Exists(full+".meta");
                if(oldHsg)AtomicBytes(full+".bamp-pair-old",File.ReadAllBytes(full));
                if(oldMeta)AtomicBytes(full+".meta.bamp-pair-old",File.ReadAllBytes(full+".meta"));
                AtomicText(full+".bamp-pair",(oldHsg?"1":"0")+(oldMeta?"1":"0"));
                try {AtomicBytes(full,data);AtomicText(full+".meta",meta);File.Delete(full+".bamp-pair");}
                catch {RecoverPair(full);throw;}
            }
        }
    }

    // Streams carry an incarnation ID, not a heuristic wall-clock reset. Old
    // incarnations remain retired until the world/connection state is reset.
    public sealed class StreamGate
    {
        private sealed class State { public string Epoch; public long Seq; public readonly HashSet<string> Retired = new HashSet<string>(); }
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();
        public bool Accept(string key, string epoch, long seq)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(epoch) || seq <= 0) return false;
            if (!states.TryGetValue(key, out var state)) { states[key] = new State { Epoch = epoch, Seq = seq }; return true; }
            if (state.Epoch != epoch)
            {
                if (state.Retired.Contains(epoch)) return false;
                state.Retired.Add(state.Epoch); state.Epoch = epoch; state.Seq = seq; return true;
            }
            if (seq <= state.Seq) return false;
            state.Seq = seq; return true;
        }
        public void Clear() => states.Clear();
    }
}
