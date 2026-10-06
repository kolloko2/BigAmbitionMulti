using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;

namespace BAMP.SyncFix
{
    public static class WorldStreams
    {
        public static bool Interior(object p,string channel)
        {
            if(p==null)return false;
            string address=(string)p.GetType().GetProperty("AddressKey")?.GetValue(p);
            return !string.IsNullOrEmpty(address)&&Wire.Fresh(p,channel+"|"+address);
        }
        public static bool AllowEnvelope(MessageEnvelope env)
        {
            if(env==null)return true;
            object p;
            if(env.Type==MessageType.InteriorSnapshot)p=env.GetPayload<InteriorSnapshotPayload>();
            else if(env.Type==MessageType.InteriorCargoSync)p=env.GetPayload<InteriorCargoSyncPayload>();
            else if(env.Type==MessageType.BuildingInteriorDelta)p=env.GetPayload<InteriorEditDeltaPayload>();
            else return true;
            return Interior(p,"interior-server");
        }
        private static readonly Dictionary<string,Tuple<string,string>> Authority=new Dictionary<string,Tuple<string,string>>();
        private static readonly object AuthorityGate=new object();
        public static void StampAuthority(CustomerSimAuthorityPayload p)
        {
            var stamp=Wire.Ensure(p);
            lock(AuthorityGate){
            if(!Authority.TryGetValue(p.AddressKey,out var old)||old.Item1!=p.SimulatorPid)
                old=Tuple.Create(p.SimulatorPid,Guid.NewGuid().ToString("N"));
            stamp.Authority=old.Item2;Authority[p.AddressKey]=old;
            }
        }
        public static void ReceivedAuthority(CustomerSimAuthorityPayload p)
        {lock(AuthorityGate)Authority[p.AddressKey]=Tuple.Create(p.SimulatorPid,Wire.Meta(p).Authority);}
        public static void StampSimulator(object p)
        {
            var type=p.GetType();var address=(string)type.GetProperty("AddressKey")?.GetValue(p);
            var stamp=Wire.Ensure(p);
            lock(AuthorityGate)if(address!=null&&Authority.TryGetValue(address,out var state))stamp.Authority=state.Item2;
        }
        public static bool ValidSimulator(object p,bool final)
        {
            var type=p.GetType();string address=(string)type.GetProperty("AddressKey")?.GetValue(p),sim=(string)type.GetProperty("SimulatorPid")?.GetValue(p);
            if(address==null||sim==null||!Wire.InWorld(Wire.Meta(p)))return false;
            if(final)
            {
                string expected=(string)Hooks.Get("CustomerPuppets","_awaitFinalFrom");
                if(sim==expected&&!string.IsNullOrEmpty(expected))return true;
            }
            lock(AuthorityGate)return Authority.TryGetValue(address,out var state)&&state.Item1==sim&&state.Item2==Wire.Meta(p).Authority;
        }
        public static void Reset(){lock(AuthorityGate)Authority.Clear();}
    }
    [HarmonyPatch]
    public static class CustomerAuthorityOrdering
    {
        private static MethodBase TargetMethod()=>Hooks.Method("CustomerPuppets","ApplyAuthority");
        private static bool Prefix(CustomerSimAuthorityPayload p)=>p!=null&&!string.IsNullOrEmpty(p.AddressKey)&&Wire.Fresh(p,"customer-authority|"+p.AddressKey);
        private static void Postfix(CustomerSimAuthorityPayload p,bool __runOriginal){if(__runOriginal)WorldStreams.ReceivedAuthority(p);}
    }
    [HarmonyPatch]
    public static class CustomerStreamOrdering
    {
        private static IEnumerable<MethodBase> TargetMethods(){yield return Hooks.Method("CustomerPuppets","ApplyState");yield return Hooks.Method("CustomerHandoff","Apply");}
        private static bool Prefix(object[] __args,MethodBase __originalMethod)
        {
            var p=__args[0];if(p==null)return false;
            bool final=p is CustomerVisitStatePayload visits&&visits.Final;
            if(!WorldStreams.ValidSimulator(p,final))return false;
            string address=(string)p.GetType().GetProperty("AddressKey").GetValue(p);
            return Wire.Fresh(p,"customers|"+__originalMethod.DeclaringType.Name+"|"+address);
        }
    }
    [HarmonyPatch]
    public static class InteriorStreamOrdering
    {
        private static IEnumerable<MethodBase> TargetMethods(){yield return Hooks.Method("InteriorSync","HandleOwnerSnapshot");yield return Hooks.Method("InteriorSync","HandleOwnerCargoSync");}
        private static bool Prefix(object[] __args)
        {
            // Filter at ingress, before updating the owner cache. Cached snapshots
            // can then be re-applied to heal a scene or graft fresh cargo.
            return WorldStreams.Interior(__args[2],"interior-owner");
        }
    }
    [HarmonyPatch]
    public static class InteriorDeltaIngress
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPServer","HandleBuildingInteriorDeltaCore");
        private static bool Prefix(InteriorEditDeltaPayload p,bool onMain)
        {if(onMain)return true;if(p==null)return false;Wire.Ensure(p);return WorldStreams.Interior(p,"interior-editor");}
    }
    [HarmonyPatch(typeof(MPLifecycle),"Reset")]
    public static class WorldStreamReset{private static void Postfix()=>WorldStreams.Reset();}
}
