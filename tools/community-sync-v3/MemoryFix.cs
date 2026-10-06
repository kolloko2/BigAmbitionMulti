using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;

namespace BAMP.SyncFix
{
    [HarmonyPatch]
    public static class FragmentBudget
    {
        private static MethodBase TargetMethod() => Hooks.Method("SteamReassembly", "TryAccept");
        private static bool Prefix(object __instance, byte[] frame, ref byte[] complete, ref bool __result,out MonitorLease __state)
        {
            __state=null;
            if (frame == null || frame.Length < 12 || frame[0] != 2 || frame[1] != 66 || frame[2] != 70 || frame[3] != 82) return true;
            var pending=(IDictionary)AccessTools.Field(__instance.GetType(),"_pending").GetValue(__instance);
            __state=new MonitorLease(pending);
            try{
            int id=BitConverter.ToInt32(frame,4);long bytes=0;
            lock(pending)
            {
                long now=DateTime.UtcNow.Ticks/10000;
                foreach(var key in pending.Keys.Cast<object>().ToArray())
                    if(now-(long)AccessTools.Field(pending[key].GetType(),"AtMs").GetValue(pending[key])>120000)pending.Remove(key);
                foreach(DictionaryEntry entry in pending) bytes+=(int)AccessTools.Field(entry.Value.GetType(),"Bytes").GetValue(entry.Value);
                if(frame.Length <= 400012 && bytes+frame.Length<=SafetyCore.MaxMessage && (pending.Contains(id)||pending.Count<32)) return true;
                pending.Remove(id);
            }
            complete=null;__result=true;Hooks.Warn("fragment memory budget exceeded; message refused");return false;
            }catch{__state.Release();throw;}
        }
        private static void Postfix(MonitorLease __state)=>__state?.Release();
        private static Exception Finalizer(Exception __exception,MonitorLease __state){__state?.Release();return __exception;}
    }

    [HarmonyPatch]
    public static class PacedBudget
    {
        private static MethodBase TargetMethod() => Hooks.Method("PacedSendQueue", "Enqueue");
        private static void Prefix(object __instance, byte[][] chunks, string supersedeKey,out MonitorLease __state)
        {
            __state=null;
            if(chunks==null)return;
            var queue=AccessTools.Field(__instance.GetType(),"_q").GetValue(__instance);
            __state=new MonitorLease(queue);
            try{
            lock(queue)
            {
                long bytes=(long)AccessTools.Field(__instance.GetType(),"_bytes").GetValue(__instance);
                foreach(var payload in (IEnumerable)queue)
                    if(!string.IsNullOrEmpty(supersedeKey) && (string)AccessTools.Field(payload.GetType(),"Key").GetValue(payload)==supersedeKey &&
                        (int)AccessTools.Field(payload.GetType(),"Next").GetValue(payload)==0)
                        bytes-=(long)AccessTools.Field(payload.GetType(),"UnsentBytes").GetValue(payload);
                if(bytes+chunks.Sum(c=>(long)(c?.Length??0))>2L*SafetyCore.MaxMessage)
                    throw new InvalidOperationException("Paced send exceeds 256 MiB; wait for queued transfer completion.");
            }
            }catch{__state.Release();throw;}
        }
        private static void Postfix(MonitorLease __state)=>__state?.Release();
        private static Exception Finalizer(Exception __exception,MonitorLease __state){__state?.Release();return __exception;}
    }

    [HarmonyPatch(typeof(MessageEnvelope), "Deserialize")]
    public static class EnvelopeBudget
    {
        private static bool Prefix(byte[] bytes, ref MessageEnvelope __result)
        {
            if(bytes!=null&&bytes.Length>0&&bytes.Length<=SafetyCore.MaxMessage)return true;
            __result=null;return false;
        }
    }
}
