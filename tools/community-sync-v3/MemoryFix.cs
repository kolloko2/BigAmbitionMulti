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
        private static bool Prefix(object __instance, byte[] frame, ref byte[] complete, ref bool __result)
        {
            // Set both out values without dereferencing an uninitialized caller slot.
            complete=null;
            __result=FragmentReceiver.Accept(__instance,frame,out var full);
            complete=full;
            return false;
        }
    }

    public static class FragmentReceiver
    {
        private sealed class Entry { public byte[][] Parts; public int Got,Bytes; public long AtMs; }
        private sealed class State { public readonly System.Collections.Generic.Dictionary<int,Entry> Pending=new System.Collections.Generic.Dictionary<int,Entry>(); public long Bytes; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,State> States=new System.Runtime.CompilerServices.ConditionalWeakTable<object,State>();
        public static bool Accept(object connection,byte[] frame,out byte[] complete)
        {
            complete=null;
            if(frame==null||frame.Length<12||frame[0]!=2||frame[1]!=66||frame[2]!=70||frame[3]!=82)return false;
            if(connection==null)return true;
            int id=BitConverter.ToInt32(frame,4),index=frame[8]|frame[9]<<8,count=frame[10]|frame[11]<<8;
            if(count<=0||count>4096||index>=count||frame.Length>400012)return true;
            var state=States.GetValue(connection,_=>new State());
            lock(state)
            {
                long now=DateTime.UtcNow.Ticks/10000;
                foreach(var key in state.Pending.Keys.ToArray())
                    if(now-state.Pending[key].AtMs>120000){state.Bytes-=state.Pending[key].Bytes;state.Pending.Remove(key);}
                Entry entry;
                if(!state.Pending.TryGetValue(id,out entry))
                {
                    if(state.Pending.Count>=32)return true;
                    entry=new Entry{Parts=new byte[count][],AtMs=now};state.Pending.Add(id,entry);
                }
                if(entry.Parts.Length!=count){state.Bytes-=entry.Bytes;state.Pending.Remove(id);return true;}
                if(entry.Parts[index]==null)
                {
                    int length=frame.Length-12;
                    if(state.Bytes+length>SafetyCore.MaxMessage){state.Bytes-=entry.Bytes;state.Pending.Remove(id);return true;}
                    var chunk=new byte[length];Buffer.BlockCopy(frame,12,chunk,0,length);
                    entry.Parts[index]=chunk;entry.Got++;entry.Bytes+=length;state.Bytes+=length;entry.AtMs=now;
                }
                try { Hooks.Call("SteamXferProgress","Report","Steam",entry.Got,count,entry.Bytes); } catch { }
                if(entry.Got!=count)return true;
                var full=new byte[entry.Bytes];int offset=0;
                foreach(var part in entry.Parts){Buffer.BlockCopy(part,0,full,offset,part.Length);offset+=part.Length;}
                state.Bytes-=entry.Bytes;state.Pending.Remove(id);complete=full;
                try { Hooks.Call("SteamXferProgress","Done"); } catch { }
                return true;
            }
        }
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
