using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json;

namespace BAMP.SyncFix
{
    public static class MergerGuard
    {
        public static string IncomingOffer = "", ConfirmedOffer = "";
        public static object Pending(string actor)
        {
            string key = (string)Hooks.Call("MPServer", "PendingKeyFor", actor);
            var pending = (IDictionary)Hooks.Get("MPServer", "_mergerPendingByTarget");
            return pending.Contains(key) ? pending[key] : null;
        }
        public static void StampState(MergerStatePayload state)
        {
            foreach (var group in state.Groups)
            {
                string signature = string.Join("|", MergerSync.StoreGroups[group.GroupId].OrderBy(p => p, StringComparer.Ordinal));
                string key = "membership|" + group.GroupId;
                var previous=Wire.Result(key);
                var parts = previous!=null ? previous.Split('\n') : new string[0];
                var id = parts.Length == 2 && parts[0] == signature ? parts[1] : Guid.NewGuid().ToString("N");
                if (parts.Length != 2 || parts[1] != id) Wire.Remember(key, signature + "\n" + id);
                Wire.Assign(group, new WireStamp { Id = id });
            }
            foreach (var offer in state.Offers)
            {
                var pending = (IDictionary)Hooks.Get("MPServer", "_mergerPendingByTarget");
                if (pending.Contains(offer.TargetKey)) Wire.Assign(offer, Wire.Ensure(pending[offer.TargetKey]));
            }
            Wire.Ensure(state);
        }
    }

    [HarmonyPatch]
    public static class MergerStateIdentity
    {
        private static MethodBase TargetMethod() => Hooks.Method("MPServer", "BuildMergerState");
        private static void Postfix(MergerStatePayload __result) { if (SaveGameManager.Current != null) MergerGuard.StampState(__result); }
    }

    [HarmonyPatch(typeof(MergerSync), "ApplyState")]
    public static class IncomingIdentity
    {
        private static void Postfix(MergerStatePayload p, bool __runOriginal)
        {
            if (!__runOriginal || p?.Offers == null) return;
            MergerGuard.IncomingOffer = p.Offers.Where(o=>o.From == MergerSync.IncomingFromPid)
                .Select(o=>Wire.Meta(o)?.Id).FirstOrDefault() ?? "";
        }
    }

    [HarmonyPatch]
    public static class MergerDialogIdentity
    {
        private static MethodBase TargetMethod() => Hooks.Method("MPCanvasUI", "ShowMergerConfirm");
        private static void Postfix(string mode) { if (mode == "accept") MergerGuard.ConfirmedOffer = MergerGuard.IncomingOffer; }
    }

    [HarmonyPatch(typeof(MPClient), "SendMergerAction")]
    public static class MergerAnswerIdentity
    {
        private static void Prefix(string action, ref string targetPid)
        {
            if (action == "accept") targetPid = "fix-offer:" + MergerGuard.ConfirmedOffer;
            if (action == "decline") targetPid = "fix-offer:" + MergerGuard.IncomingOffer;
        }
    }

    [HarmonyPatch(typeof(MPServer), "HostMergerAction")]
    public static class MergerAnswerGate
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(string action, ref string targetPid, string actorPid)
        {
            if (action != "accept" && action != "decline") return true;
            string id = targetPid != null && targetPid.StartsWith("fix-offer:") ? targetPid.Substring(10) : "";
            if (actorPid == MPConfig.PlayerId && id == "")
                id = action == "accept" ? MergerGuard.ConfirmedOffer : MergerGuard.IncomingOffer;
            var pending = MergerGuard.Pending(actorPid);
            if (pending == null || id == "" || Wire.Ensure(pending).Id != id)
            {
                Hooks.Warn("stale merger answer refused; company membership unchanged");
                MPServer.RebroadcastMergerState(true); return false;
            }
            targetPid = ""; return true;
        }
    }

    [HarmonyPatch(typeof(MPServer), "HostMergerAction")]
    public static class MergerAcceptanceRollback
    {
        public sealed class Snapshot
        {
            public readonly List<Tuple<string,string,object,string>> Fields = new List<Tuple<string,string,object,string>>();
            public readonly List<Tuple<FieldInfo,object>> Scalars = new List<Tuple<FieldInfo,object>>();
            public GameInstance Save; public float Money; public Dictionary<string,string> ModData;
            public FieldInfo ManifestField;public object Manifest;
        }
        private static void Prefix(string action, out Snapshot __state)
        {
            __state = null; if (action != "accept") return;
            __state = new Snapshot();
            __state.Save=SaveGameManager.Current;
            if(__state.Save!=null){__state.Money=__state.Save.Money;__state.ModData=__state.Save.modData==null?null:new Dictionary<string,string>(__state.Save.modData);}
            __state.ManifestField=Hooks.Type("MPSaveCoordinator").GetField("_activeManifest",BindingFlags.NonPublic|BindingFlags.Static);
            var manifest=__state.ManifestField.GetValue(null);
            __state.Manifest=manifest==null?null:JsonConvert.DeserializeObject(JsonConvert.SerializeObject(manifest),manifest.GetType());
            foreach (var type in new[]{"MergerSync","GrantSync"})
                foreach(var field in Hooks.Type(type).GetFields(BindingFlags.NonPublic|BindingFlags.Static))
                    if(typeof(IDictionary).IsAssignableFrom(field.FieldType))
                    { var obj=field.GetValue(null); __state.Fields.Add(Tuple.Create(type,field.Name,obj,JsonConvert.SerializeObject(obj))); }
                    else if(!field.IsInitOnly&&!field.IsLiteral&&(field.FieldType.IsPrimitive||field.FieldType==typeof(string)))
                        __state.Scalars.Add(Tuple.Create(field,field.GetValue(null)));
            foreach(var name in new[]{"_mergerPendingByTarget","_mergerCooldown","_walletBalance","_walletContributed","CashByStableId"})
            { var obj=Hooks.Get("MPServer",name); __state.Fields.Add(Tuple.Create("MPServer",name,obj,JsonConvert.SerializeObject(obj))); }
            MergerOutbox.Begin();
        }
        private static void Postfix(Snapshot __state){if(__state!=null)MergerOutbox.Commit();}
        private static Exception Finalizer(Exception __exception, Snapshot __state)
        {
            if (__exception == null || __state == null) return __exception;
            MergerOutbox.Cancel();
            foreach(var item in __state.Fields)
            {
                var dictionary=(IDictionary)item.Item3;
                var restored=(IDictionary)JsonConvert.DeserializeObject(item.Item4,item.Item3.GetType());
                dictionary.Clear();foreach(DictionaryEntry entry in restored)dictionary.Add(entry.Key,entry.Value);
            }
            foreach(var item in __state.Scalars)item.Item1.SetValue(null,item.Item2);
            __state.ManifestField.SetValue(null,__state.Manifest);
            if(ReferenceEquals(__state.Save,SaveGameManager.Current)&&__state.Save!=null)
            {__state.Save.Money=__state.Money;__state.Save.modData=__state.ModData;SaveGameManager.MarkChange();}
            MPServer.RebroadcastMergerState(true);
            Hooks.Warn("merger acceptance failed; server ledgers restored: " + __exception);
            return null;
        }
    }
}
