using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BigAmbitionsMP;
using Extensions;
using HarmonyLib;
using UnityEngine;

namespace BAMP.SyncFix
{
    public sealed class MusicTransferPayload
    {
        public WireStamp FixStamp;
        public string Kind, Hash, Extension, Name;
        public int Total, Offset;
        public byte[] Bytes;
    }

    // Host-selected LocalFiles song, pulled by clients in bounded, paced chunks.
    // Only the currently selected game's RadioClip can be exported; no peer path
    // or filename is ever used to read a file. Imported audio stays in a cache.
    public static class MusicTransfer
    {
        private const MessageType Channel = (MessageType)223;
        private const int Chunk = 32768, MaxSong = 16 * 1024 * 1024;
        private const long CacheBudget = 256L * 1024 * 1024;
        private sealed class Song
        {
            public string Hash, Extension, Name;
            public byte[] Bytes;
        }
        private sealed class Download
        {
            public MusicTransferPayload Header;
            public MemoryStream Data = new MemoryStream();
        }
        private static Song _song, _previousSong;
        private static string _source, _world, _imported;
        private static Task<Song> _reading;
        private static Task<string> _writing;
        private static Download _download;
        private static MusicTransferPayload _installHeader;
        private static float _nextTick, _nextRead, _awaitUntil;
        private static readonly Dictionary<int, float> Requests = new Dictionary<int, float>();
        private static readonly HashSet<string> BadCache = new HashSet<string>();
        private static readonly HashSet<string> Warned = new HashSet<string>();
        private static RadioPlayer _importPlayer;
        private static RadioStationData _originalStation;
        private static RadioStationData _sharedStation;

        private static string Cache => Path.Combine(RadioPlayer.GetRadioPath(), "BAMP-shared-cache");
        private static string Digest(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
        private static bool ValidHash(string hash) => hash != null && hash.Length == 64
            && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        private static bool ValidExtension(string ext) => new[] { ".mp3", ".wav", ".ogg", ".flac", ".aif", ".aiff" }.Contains(ext);
        private static bool HeaderValid(MusicTransferPayload p) => p != null && ValidHash(p.Hash)
            && ValidExtension(p.Extension) && p.Total > 0 && p.Total <= MaxSong
            && p.Name != null && p.Name.Length <= 128;
        private static void Notice(string key, string message)
        {
            if (Warned.Add(key)) Hooks.Warn("[MusicTransfer] " + message);
        }
        private static bool Wanted()
        {
            var bm = InstanceBehavior<BuildingManager>.Instance;
            return bm?.buildingRegistration != null
                && bm.buildingRegistration.GetBusinessRadioStation() == RadioStation.LocalFiles;
        }
        private static MessageEnvelope Envelope(MusicTransferPayload p) => MessageEnvelope.Create(Channel, MPConfig.PlayerId, p);

        public static void Tick()
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            var world = Wire.CurrentWorld;
            if (string.IsNullOrEmpty(world) || SaveGameManager.Current == null) return;
            if (_world != world) { Reset(); _world = world; }
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 0.2f;
            try
            {
                if (MPServer.IsRunning) { ReadHostSong(); return; }
                CompleteImport();
                if (_importPlayer != null && _sharedStation != null
                    && _importPlayer.GetRadioStationData(RadioStation.LocalFiles) != _sharedStation)
                {
                    _originalStation = _importPlayer.GetRadioStationData(RadioStation.LocalFiles);
                    _sharedStation = null; _imported = null;
                }
                if (!Wanted() || _writing != null || Time.unscaledTime < _awaitUntil) return;
                _awaitUntil = Time.unscaledTime + 3f; // one request in flight; retry after loss
                if (_download == null)
                    MPClient.SendEnvelope(Envelope(new MusicTransferPayload { Kind = "poll" }));
                else
                    MPClient.SendEnvelope(Envelope(new MusicTransferPayload {
                        Kind = "chunk", Hash = _download.Header.Hash, Offset = (int)_download.Data.Length
                    }));
            }
            catch (Exception ex) { Notice(ex.GetType().Name, ex.Message); }
        }

        private static void ReadHostSong()
        {
            if (_reading != null)
            {
                if (!_reading.IsCompleted) return;
                if (_reading.IsFaulted) { Notice("read", "could not read selected song: " + _reading.Exception.GetBaseException().Message); _song = null; }
                else _song = _reading.Result;
                _reading = null;
            }
            if (Time.unscaledTime < _nextRead) return;
            _nextRead = Time.unscaledTime + 1f;
            var station = InstanceBehavior<GameManager>.Instance?.radioPlayer?.GetRadioStationData(RadioStation.LocalFiles);
            if (station?.radioClips == null || station.radioClips.Length == 0 || !station.HasPlayableClips) { _song = null; return; }
            if (station.currentClipIndex < 0 || station.currentClipIndex >= station.radioClips.Length) return;
            var clip = station.radioClips[station.currentClipIndex];
            if (clip == null || clip.HasLoadFailed || string.IsNullOrEmpty(clip.path)) return;
            string path = Path.GetFullPath(clip.path);
            string root = Path.GetFullPath(RadioPlayer.GetRadioPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!ValidExtension(ext)) return;
            var info = new FileInfo(path);
            string source = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (_source == source) return;
            _source = source;
            if (_song != null) _previousSong = _song;
            _song = null;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > 128) name = name.Substring(0, 128);
            _reading = Task.Run(() => {
                // Check both before and after reading; files may change mid-read.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length <= 0 || stream.Length > MaxSong) throw new IOException("song exceeds 16 MiB transfer limit");
                    var bytes = new byte[(int)stream.Length];
                    int got = 0;
                    while (got < bytes.Length)
                    {
                        int n = stream.Read(bytes, got, bytes.Length - got);
                        if (n == 0) throw new EndOfStreamException();
                        got += n;
                    }
                    return new Song { Bytes = bytes, Hash = Digest(bytes), Extension = ext, Name = name };
                }
            });
        }

        public static void HostReceive(MPLink peer, MusicTransferPayload p)
        {
            if (!MPServer.IsRunning || !Wire.InWorld(p?.FixStamp)) return;
            var names = (ConcurrentDictionary<int, string>)Hooks.Get("MPServer", "_peerNames");
            var banned = (ConcurrentDictionary<int, byte>)Hooks.Get("MPServer", "_banLinks");
            if (!names.ContainsKey(peer.Id) || banned.ContainsKey(peer.Id) || !peer.IsAlive) return;
            if (Requests.TryGetValue(peer.Id, out float last) && Time.unscaledTime - last < 0.15f) return;
            Requests[peer.Id] = Time.unscaledTime;
            var song = p.Kind == "chunk" && p.Hash == _previousSong?.Hash ? _previousSong : _song;
            if (song == null) return;
            var answer = new MusicTransferPayload { Kind = "header", Hash = song.Hash,
                Extension = song.Extension, Name = song.Name, Total = song.Bytes.Length };
            if (p.Kind == "chunk" && p.Hash == song.Hash && p.Offset >= 0 && p.Offset < song.Bytes.Length)
            {
                answer.Kind = "data"; answer.Offset = p.Offset;
                answer.Bytes = new byte[Math.Min(Chunk, song.Bytes.Length - p.Offset)];
                Buffer.BlockCopy(song.Bytes, p.Offset, answer.Bytes, 0, answer.Bytes.Length);
            }
            else if (p.Kind != "poll" && p.Kind != "chunk") return;
            peer.SendPaced(Envelope(answer).Serialize());
        }

        public static void ClientReceive(MusicTransferPayload p)
        {
            if (!MPClient.IsClientInWorld || !Wire.InWorld(p?.FixStamp) || !HeaderValid(p) || !Wanted() || _writing != null) return;
            if (p.Hash == _imported) return;
            if (_download == null || _download.Header.Hash != p.Hash)
            {
                _download?.Data.Dispose();
                _download = new Download { Header = new MusicTransferPayload { Hash = p.Hash, Extension = p.Extension,
                    Name = p.Name, Total = p.Total, FixStamp = p.FixStamp } };
                _awaitUntil = 0;
                if (Directory.Exists(Cache) && new DirectoryInfo(Cache).GetFiles().Sum(f => f.Length) + p.Total > CacheBudget)
                { Notice("budget", "shared audio cache reached its 256 MiB limit"); _download.Data.Dispose(); _download = null; return; }
                string cached = Path.Combine(Cache, p.Hash + p.Extension);
                if (!BadCache.Contains(p.Hash) && File.Exists(cached)) { FinishDownload(null, cached); return; }
            }
            if (p.Kind != "data" || p.Total != _download.Header.Total || p.Extension != _download.Header.Extension
                || p.Bytes == null || p.Bytes.Length == 0 || p.Bytes.Length > Chunk
                || p.Offset != _download.Data.Length || p.Bytes.Length > p.Total - p.Offset) return;
            _download.Data.Write(p.Bytes, 0, p.Bytes.Length);
            _awaitUntil = 0;
            if (_download.Data.Length == p.Total) FinishDownload(_download.Data.ToArray(), null);
        }

        private static void FinishDownload(byte[] bytes, string existing)
        {
            var header = _download.Header;
            _download.Data.Dispose(); _download = null;
            _installHeader = header;
            string cache = Cache;
            _writing = Task.Run(() => {
                if (existing != null)
                {
                    var info = new FileInfo(existing);
                    if (info.Length != header.Total || info.Length > MaxSong || Digest(File.ReadAllBytes(existing)) != header.Hash)
                        throw new IOException("cached song failed hash verification");
                    return existing;
                }
                if (Digest(bytes) != header.Hash) throw new IOException("received song failed hash verification");
                Directory.CreateDirectory(cache);
                string target = Path.Combine(cache, header.Hash + header.Extension);
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return target;
            });
        }

        private static void CompleteImport()
        {
            if (_writing == null || !_writing.IsCompleted) return;
            var task = _writing; _writing = null;
            if (task.IsFaulted) { BadCache.Add(_installHeader.Hash); Notice("write", "could not cache shared song: " + task.Exception.GetBaseException().Message); return; }
            var player = InstanceBehavior<GameManager>.Instance?.radioPlayer;
            if (player == null || !Wire.InWorld(_installHeader.FixStamp)) return;
            var stations = (Dictionary<RadioStation, RadioStationData>)AccessTools.Field(typeof(RadioPlayer), "_radioStationsData").GetValue(player);
            var original = player.GetRadioStationData(RadioStation.LocalFiles);
            if (_importPlayer != player) { _importPlayer = player; _originalStation = original; }
            // Session-only playlist copy. Keep the user's original clips and files.
            var clips = new List<RadioClip> { new RadioClip { Name = _installHeader.Name, path = task.Result,
                type = AudioFileFormatHelper.GetAudioTypeFromExtension(task.Result) } };
            if (_originalStation?.radioClips != null) clips.AddRange(_originalStation.radioClips);
            player.onSongsRefreshing.Invoke();
            if (_sharedStation?.radioClips?.Length > 0 && _sharedStation.radioClips[0].clip != null)
                UnityEngine.Object.Destroy(_sharedStation.radioClips[0].clip);
            _sharedStation = new RadioStationData(RadioStation.LocalFiles, clips.ToArray());
            stations[RadioStation.LocalFiles] = _sharedStation;
            _imported = _installHeader.Hash;
            player.onSongsLoaded.Invoke();
            Hooks.Warn("[MusicTransfer] host song cached and added to local radio: " + _installHeader.Name);
        }

        public static void Reset()
        {
            // Restore the real local playlist; cached files remain reusable.
            try
            {
                if (_importPlayer != null && _originalStation != null && _sharedStation != null)
                {
                    var stations = (Dictionary<RadioStation, RadioStationData>)AccessTools.Field(typeof(RadioPlayer), "_radioStationsData").GetValue(_importPlayer);
                    if (stations.TryGetValue(RadioStation.LocalFiles, out var current) && current == _sharedStation)
                    {
                        // Scene teardown may already have destroyed speaker sources.
                        // Restore data here; normal game load/entry handles playback.
                        stations[RadioStation.LocalFiles] = _originalStation;
                    }
                }
            }
            catch (Exception ex) { Notice("reset", "playlist restore: " + ex.Message); }
            _download?.Data.Dispose(); _download = null;
            _reading = null; _writing = null; _song = null; _previousSong = null; _source = null; _world = null; _imported = null;
            _importPlayer = null; _originalStation = null; _sharedStation = null; _installHeader = null;
            _nextTick = _nextRead = _awaitUntil = 0;
            Requests.Clear(); BadCache.Clear(); Warned.Clear();
        }

        public static bool IsTransfer(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 65536) return false;
            // Match native envelope framing without reflection on every game packet.
            if (bytes.Length > 6 && bytes[0] == 2 && bytes[1] == 'B' && bytes[2] == 'Z'
                && (bytes[3] == 'P' || bytes[3] == 'A')) return (bytes[4] | (bytes[5] << 8)) == (int)Channel;
            if (bytes.Length < 7 || bytes[0] != '{' || bytes[1] != '"' || bytes[2] != 't'
                || bytes[3] != '"' || bytes[4] != ':') return false;
            int type = 0, at = 5;
            while (at < bytes.Length && bytes[at] >= '0' && bytes[at] <= '9' && type <= 255)
                type = type * 10 + bytes[at++] - '0';
            return type == (int)Channel;
        }
    }

    [HarmonyPatch(typeof(MPCanvasUI), "Update")]
    public static class MusicTransferTick { private static void Postfix() => MusicTransfer.Tick(); }

    [HarmonyPatch(typeof(MPLifecycle), "Reset")]
    public static class MusicTransferReset { private static void Postfix() => MusicTransfer.Reset(); }

    [HarmonyPatch(typeof(MPServer), "OnReceive")]
    public static class MusicTransferHostIngress
    {
        private static bool Prefix(MPLink peer, byte[] bytes)
        {
            if (!MusicTransfer.IsTransfer(bytes)) return true;
            var env = MessageEnvelope.Deserialize(bytes);
            if (env == null || env.Data.Length > 50000) return false;
            var p = env.GetPayload<MusicTransferPayload>();
            GameStatePatcher.EnqueueOnMainThread(() => MusicTransfer.HostReceive(peer, p));
            return false;
        }
    }

    [HarmonyPatch(typeof(MPClient), "OnReceive")]
    public static class MusicTransferClientIngress
    {
        private static bool Prefix(byte[] bytes)
        {
            if (!MusicTransfer.IsTransfer(bytes)) return true;
            var env = MessageEnvelope.Deserialize(bytes);
            if (env == null || env.Data.Length > 50000) return false;
            var p = env.GetPayload<MusicTransferPayload>();
            GameStatePatcher.EnqueueOnMainThread(() => MusicTransfer.ClientReceive(p));
            return false;
        }
    }
}
