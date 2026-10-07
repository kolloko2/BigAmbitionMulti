using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json;

namespace BAMP.SyncFix
{
    public sealed class WireStamp
    {
        public string Id, Epoch, World, Origin, Group, Members, Authority;
        public long Sequence;
    }
    public static class Wire
    {
        public static string CurrentWorld => MPServer.IsRunning
            ? (!string.IsNullOrEmpty(MPSaveCoordinator.ActivePlaythroughId)
                ? MPSaveCoordinator.ActivePlaythroughId : MPSaveManager.ActivePlaythrough ?? "")
            : MPSaveManager.ActivePlaythrough ?? "";
        public static string Epoch = Guid.NewGuid().ToString("N");
        private static long sequence;
        private static readonly StreamGate Streams = new StreamGate();
        private static readonly object StreamLock = new object();
        private const string JournalKey = "GoingPublic.SyncFix.1";
        private static readonly ConditionalWeakTable<object, WireStamp> Local = new ConditionalWeakTable<object, WireStamp>();
        public static WireStamp Meta(object payload)
        {
            if (payload == null) return null;
            var field = payload.GetType().GetField("FixStamp");
            if (field != null) return (WireStamp)field.GetValue(payload);
            return Local.TryGetValue(payload, out var stamp) ? stamp : null;
        }
        public static void Assign(object payload, WireStamp stamp)
        {
            var field = payload.GetType().GetField("FixStamp");
            if (field != null) field.SetValue(payload, stamp);
            else { Local.Remove(payload); Local.Add(payload, stamp); }
        }
        public static string Members(string group)
        {
            var groups = (IDictionary)Hooks.Get("MergerSync", "_groupInfo");
            if (!groups.Contains(group)) return "";
            var info = (MergerGroupInfo)groups[group];
            return string.Join("|", info.MemberPids.OrderBy(p => p, StringComparer.Ordinal)) + "#" + (Meta(info)?.Id ?? "");
        }
        public static WireStamp New()
        {
            string group = MergerSync.MyGroupId;
            return new WireStamp { Id = Guid.NewGuid().ToString("N"), Epoch = Epoch,
                World = Wire.CurrentWorld, Origin = MPConfig.PlayerId,
                Group = group, Members = Members(group), Sequence = Interlocked.Increment(ref sequence) };
        }
        public static WireStamp Ensure(object payload)
        {
            var stamp = Meta(payload);
            if (stamp == null) { stamp = New(); Assign(payload, stamp); }
            return stamp;
        }
        public static void StampEnvelope(MessageEnvelope envelope, object payload)
        {
            if (payload == null || payload.GetType().GetField("FixStamp") == null) return;
            var stamp = Ensure(payload);
            if(stamp.Origin==MPConfig.PlayerId)
            {
                if(payload is CustomerSimAuthorityPayload authority)WorldStreams.StampAuthority(authority);
                else if(payload is CustomerPuppetStatePayload||payload is CustomerVisitStatePayload)WorldStreams.StampSimulator(payload);
            }
            if(envelope.Type==MessageType.RentRequest&&payload is BuildingOwnershipPayload rental&&SaveGameManager.Current!=null)
            {
                Remember("rent-current|"+rental.AddressKey,stamp.Id);
                RentRecovery.Requested(rental.AddressKey,stamp.Id);
            }
            // Relays retain the originating sequence. Local creation/re-sends use
            // a fresh sequence while preserving the logical operation ID.
            if (stamp.Origin == MPConfig.PlayerId) stamp.Sequence = Interlocked.Increment(ref sequence);
            envelope.Data = JsonConvert.SerializeObject(payload);
        }
        public static bool InWorld(WireStamp stamp)
        {
            var world = Wire.CurrentWorld;
            return stamp != null && !string.IsNullOrEmpty(stamp.Id) && (world == "" || stamp.World == world);
        }
        public static bool Fresh(object payload, string channel)
        {
            var stamp = Meta(payload);
            if (!InWorld(stamp)) return false;
            lock (StreamLock) return Streams.Accept(channel + "|" + stamp.Origin, stamp.Epoch, stamp.Sequence);
        }
        public static Dictionary<string, string> Journal()
        {
            return JournalCache.Snapshot();
        }
        public static string Result(string key) => JournalCache.Result(key);
        public static KeyValuePair<string,string>[] Scan(string prefix)=>JournalCache.Scan(prefix);
        public static void Remember(string key, string result)
        {
            JournalCache.Remember(key,result);
        }
        public static void ApplyMoney(MoneyAdjustPayload p)
        {
            var stamp = Meta(p);
            if (p == null || SaveGameManager.Current==null || p.To != MPConfig.PlayerId || !InWorld(stamp) || !SafetyCore.Finite(p.Amount) || !SafetyCore.Finite(SaveGameManager.Current.Money+p.Amount)) return;
            var key = "money|" + stamp.Id;
            if (Result(key) != null)
            {
                var wallet=Result("money-wallet|"+stamp.Id);
                if(wallet!=null)
                {
                    var replay=JsonConvert.DeserializeObject<MergerWalletDeltaPayload>(wallet);
                    replay.PlayerId=MPConfig.PlayerId;
                    if(MPServer.IsRunning)ApplyWallet(replay);
                    else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerWalletDelta,MPConfig.PlayerId,replay));
                }
                MoneyTransport.Receipt(p); return;
            }
            var old=MoneyTransport.Applying;MoneyTransport.Applying=p;
            try { MPHub.ApplyMoneyDelta(p.Amount, p.Reason, !p.Silent); }
            finally { MoneyTransport.Applying=old; }
            Remember(key, "done");
            MoneyTransport.Receipt(p);
        }
        public static void ApplyWallet(MergerWalletDeltaPayload p)
        {
            var stamp = Meta(p);
            if (p == null || !InWorld(stamp) || !SafetyCore.Finite(p.Amount)) return;
            var stable = (string)Hooks.Call("MPServer", "StableOfPid", p.PlayerId);
            var group = MergerSync.GroupOfStable(stable);
            if (group != stamp.Group || Members(group) != stamp.Members) { Hooks.Warn("old-group wallet operation refused"); return; }
            var key = "wallet|" + stamp.Id;
            if (Result(key) != null) return;
            MPServer.HostWalletDelta(p.PlayerId, p.Amount, p.Key, p.Contribution);
            Remember(key, "done");
        }
        public static void Reset()
        {
            Epoch = Guid.NewGuid().ToString("N"); Interlocked.Exchange(ref sequence, 0);
            lock (StreamLock) Streams.Clear();
        }
    }

    [HarmonyPatch(typeof(MPLifecycle), "Reset")]
    public static class WireReset { private static void Postfix() => Wire.Reset(); }

    [HarmonyPatch]
    public static class SnapshotGate
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return Hooks.Method("MergerSync", "ApplyState");
            yield return Hooks.Method("MergerWallet", "ApplyState");
            yield return Hooks.Method("RemotePlayerManager", "SpawnOrUpdate");
            yield return Hooks.Method("VehicleManager", "ApplyDriveSync");
        }
        private static bool Prefix(object[] __args, MethodBase __originalMethod)
        {
            var payload = __args[0]; if (payload == null) return false;
            if(payload is MergerWalletStatePayload wallet&&!SafetyCore.Finite(wallet.Balance))return false;
            // Locally constructed authoritative states have not crossed the wire.
            // Stamp them here; a received old-format object cannot enter protocol 128.
            Wire.Ensure(payload);
            string key = __originalMethod.DeclaringType.Name;
            var id = payload.GetType().GetProperty("PlayerId") ?? payload.GetType().GetProperty("VehicleId") ?? payload.GetType().GetProperty("GroupId");
            // Personal payout and group reconciliation share one ordering
            // channel: an old payout must not overwrite a newer group mirror.
            if (id != null && __originalMethod.DeclaringType.Name!="MergerWallet") key += "|" + id.GetValue(payload);
            return Wire.Fresh(payload, key);
        }
    }

    [HarmonyPatch]
    public static class QueueGeneration
    {
        private static MethodBase TargetMethod() => Hooks.Method("GameStatePatcher", "EnqueueInternal");
        private static void Prefix(ref Action action, ref string key)
        {
            if (action == null) return;
            var old = action; var save = SaveGameManager.Current;
            var merger=MergerOutbox.Current;
            string world = Wire.CurrentWorld, epoch = Wire.Epoch;
            if (key == null && old.Target != null)
                foreach (var field in old.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.FieldType == typeof(PlayerPositionPayload) && field.GetValue(old.Target) is PlayerPositionPayload position)
                    { key = "syncfix-player|" + position.PlayerId; break; }
            action = () => {
                if ((merger!=null&&merger.Canceled)||epoch != Wire.Epoch || (save != null && !ReferenceEquals(save, SaveGameManager.Current)) ||
                    (world.Length > 0 && world != (Wire.CurrentWorld))) return;
                old();
            };
        }
    }

    [HarmonyPatch]
    public static class StorageJournal
    {
        public sealed class OwnerAttempt{public string Key;public CargoRollback Baseline;}
        private static MethodBase TargetMethod() => Hooks.Method("StorageSync", "OwnerApply");
        private static bool Prefix(StorageOpPayload req, ref StorageResPayload __result, out OwnerAttempt __state)
        {
            __state = null; if (req == null) return true;
            if(SaveGameManager.Current==null){__result=new StorageResPayload{Reason="not-loaded"};return false;}
            var stamp = Wire.Ensure(req); if (!Wire.InWorld(stamp)) { __result = new StorageResPayload { Reason = "old-world" }; return false; }
            __state = new OwnerAttempt{Key="storage-owner|" + stamp.Id};
            var result = Wire.Result(__state.Key);
            if (result == null){__state.Baseline=CargoRollback.Owner(req);return true;}
            __result = JsonConvert.DeserializeObject<StorageResPayload>(result); return false;
        }
        private static void Postfix(StorageOpPayload req, StorageResPayload __result, OwnerAttempt __state)
        {
            if (__state == null || __result == null) return;
            if(!__result.Ok)__state.Baseline?.Restore();
            Wire.Assign(__result, Wire.Meta(req));
            Wire.Remember(__state.Key, JsonConvert.SerializeObject(__result));
        }
        private static Exception Finalizer(Exception __exception,OwnerAttempt __state){if(__exception!=null)__state?.Baseline?.Restore();return __exception;}
    }

    [HarmonyPatch]
    public static class StorageResultJournal
    {
        private static MethodBase TargetMethod() => Hooks.Method("StorageSync", "OnResult");
        private static bool Prefix(StorageResPayload res, out StorageProgress.Frame __state)
        {
            __state = null; if (res == null || !Wire.InWorld(Wire.Meta(res))) return false;
            string key="storage-result|"+Wire.Meta(res).Id;
            if(Wire.Result(key)=="consumed")return false;
            __state=StorageProgress.Begin(res);
            return true;
        }
        private static void Postfix(StorageProgress.Frame __state) {StorageProgress.End(__state);}
        private static Exception Finalizer(Exception __exception,StorageProgress.Frame __state)
        {if(__exception!=null)StorageProgress.Fail();StorageProgress.End(__state);return __exception;}
    }

    [HarmonyPatch]
    public static class StorageResponseIdentity
    {
        private static MethodBase TargetMethod() => Hooks.Method("StorageSync", "ResFrom");
        private static void Postfix(StorageOpPayload req, StorageResPayload __result) => Wire.Assign(__result, Wire.Ensure(req));
    }

    [HarmonyPatch]
    public static class LocalWalletIdentity
    {
        private static MethodBase TargetMethod() => Hooks.Method("MergerWallet", "Forward");
        private static bool Prefix(float amount, string key, bool contribution, PwTransaction tx)
        {
            if (!MPServer.IsRunning && MoneyTransport.Applying==null) return true;
            var payload = new MergerWalletDeltaPayload { PlayerId = MPConfig.PlayerId, Amount = amount, Key = key, Contribution = contribution, Tx = tx };
            var stamp=Wire.Ensure(payload);
            if(MoneyTransport.Applying!=null)
            {
                var moneyId=Wire.Meta(MoneyTransport.Applying).Id;stamp.Id="money-linked|"+moneyId;
                Wire.Remember("money-wallet|"+moneyId,JsonConvert.SerializeObject(payload));
            }
            if(!MPServer.IsRunning)
            {MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerWalletDelta,MPConfig.PlayerId,payload));return false;}
            Wire.ApplyWallet(payload);
            if (tx != null) Hooks.Call("CompanyFeed", "HostIngest", tx, MPConfig.PlayerId);
            return false;
        }
    }
}
