using System;
using System.Collections.Generic;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;

namespace BAMP.SyncFix
{
    public static class MergerOutbox
    {
        [ThreadStatic] private static List<Action> pending;
        [ThreadStatic] private static bool persist;
        public sealed class Token{public bool Canceled;}
        [ThreadStatic] public static Token Current;
        public static bool Active=>pending!=null;
        public static void Begin(){if(pending!=null)throw new InvalidOperationException("Nested merger commit refused.");pending=new List<Action>();persist=false;Current=new Token();}
        public static bool Queue(Action send){if(pending==null)return false;pending.Add(send);return true;}
        public static bool DeferPersist(){if(pending==null)return false;persist=true;return true;}
        public static void Cancel(){if(Current!=null)Current.Canceled=true;Current=null;pending=null;persist=false;}
        public static void Commit()
        {
            var sends=pending;bool save=persist;var token=Current;
            pending=null;persist=false;
            // Complete the authoritative state before exposing any intermediate
            // membership/wallet/access message to a remote player.
            try{if(save)CheckedPersistence.Run(()=>MPSaveCoordinator.PersistGrantsNow());}
            catch{if(token!=null)token.Canceled=true;Current=null;throw;}
            Current=null;
            if(sends==null)return;
            foreach(var send in sends)try{send();}catch(Exception ex){Hooks.Warn("merger broadcast retry needed: "+ex.GetBaseException().Message);}
            try{MPServer.RebroadcastMergerState(true);}catch(Exception ex){Hooks.Warn("merger state rebroadcast pending: "+ex.GetBaseException().Message);}
        }
        public static MessageEnvelope Copy(MessageEnvelope env)=>new MessageEnvelope {Type=env.Type,SenderId=env.SenderId,Data=env.Data,Attachment=env.Attachment==null?null:(byte[])env.Attachment.Clone()};
    }
    [HarmonyPatch]
    public static class MergerBufferedBroadcast
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPServer","Broadcast");
        private static bool Prefix(MessageEnvelope env)
        {if(!MergerOutbox.Active)return true;var snapshot=MergerOutbox.Copy(env);return !MergerOutbox.Queue(()=>Hooks.Call("MPServer","Broadcast",snapshot));}
    }
    [HarmonyPatch]
    public static class MergerBufferedSend
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPServer","Send");
        private static bool Prefix(MPLink peer,MessageEnvelope env)
        {if(!MergerOutbox.Active)return true;var snapshot=MergerOutbox.Copy(env);return !MergerOutbox.Queue(()=>Hooks.Call("MPServer","Send",peer,snapshot));}
    }
    [HarmonyPatch(typeof(MPSaveCoordinator),"PersistGrantsNow")]
    public static class MergerBufferedPersistence{private static bool Prefix()=>!MergerOutbox.DeferPersist();}
    [HarmonyPatch(typeof(MPLifecycle),"Reset")]
    public static class MergerOutboxReset{private static void Postfix()=>MergerOutbox.Cancel();}
}
