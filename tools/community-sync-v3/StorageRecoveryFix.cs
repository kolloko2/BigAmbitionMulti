using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using BigAmbitions.Items;
using HarmonyLib;
using Helpers;
using Newtonsoft.Json;
using UnityEngine;

namespace BAMP.SyncFix
{
    public static class StorageProgress
    {
        public sealed class Frame {public Frame Previous;public StorageResPayload Result;public bool Failed,Committed,Ended;public string Stage;}
        [ThreadStatic] private static Frame current;
        private static readonly Dictionary<string,CargoInstance> Places=new Dictionary<string,CargoInstance>();
        private static float nextAt;
        public static Frame Begin(StorageResPayload result)
        {var frame=new Frame {Previous=current,Result=result};current=frame;return frame;}
        public static void Fail(){if(current!=null)current.Failed=true;}
        public static void Commit(){if(current!=null&&current.Stage=="effect")current.Committed=true;}
        public static void CommitIf(bool success){if(success)Commit();}
        public static void End(Frame frame)
        {
            if(frame==null||frame.Ended)return;frame.Ended=true;current=frame.Previous;
            string id=Wire.Meta(frame.Result).Id;
            if(!frame.Failed)
            {Wire.Remember("storage-result|"+id,"consumed");Wire.Remember("pending-storage|"+id,"done");Places.Remove(id);}
            else Hooks.Warn("storage result "+id+" is pending; completed stages will not be replayed");
        }
        private static void Run(StorageResPayload res,string stage,string method)
        {
            if(current==null){Hooks.Call("StorageSync",method,res);return;}
            string key="storage-stage|"+Wire.Meta(res).Id+"|"+stage;
            if(Wire.Result(key)=="done")return;
            bool prior=current.Failed;current.Failed=false;current.Committed=false;current.Stage=stage;
            float before=SaveGameManager.Current.Money;
            try{Hooks.Call("StorageSync",method,res);}catch(Exception ex){Fail();Hooks.Warn("storage "+stage+": "+ex.GetBaseException().Message);}
            bool committed=current.Committed||(stage=="effect"&&SaveGameManager.Current.Money!=before);
            if(stage=="replica"&&current.Failed)
            {
                // Replica echo is display-only. Rebuild from owner truth instead
                // of replaying a partially applied visual reduction.
                try {if(MPClient.IsConnected&&!MPServer.IsRunning&&!string.IsNullOrEmpty(res.AddressKey)){GameStatePatcher.ForgetInteriorBaseline(res.AddressKey);MPClient.SendInteriorRequest(res.AddressKey);}}
                catch(Exception ex){Hooks.Warn(ex.Message);}
                current.Failed=false;
            }
            if(!current.Failed||committed){Wire.Remember(key,"done");current.Failed=false;}
            current.Failed|=prior;current.Stage=null;
        }
        public static void Echo(StorageResPayload res)=>Run(res,"replica","EchoBuildingReplica");
        public static void Take(StorageResPayload res)=>Run(res,"effect","OnTakeResult");
        public static void Put(StorageResPayload res)=>Run(res,"effect","OnPutResult");
        public static void Sending(StorageOpPayload req)
        {
            var stamp=Wire.Ensure(req);
            if(current!=null&&(req.Ctx=="return"||req.Ctx=="boxreturn"||req.Ctx=="stationreturn"))
            {stamp.Id=Wire.Meta(current.Result).Id+"|return|"+req.Ctx;}
            if(req.Ctx=="placereduce"&&!Places.ContainsKey(stamp.Id))
            {
                var place=(CargoInstance)Hooks.Get("StorageSync","_pendingPlace");
                if(place!=null)Places[stamp.Id]=place;
            }
            if(Wire.Result("pending-storage|"+stamp.Id)!="done")Wire.Remember("pending-storage|"+stamp.Id,JsonConvert.SerializeObject(req));
        }
        public static object Placement(StorageResPayload res)
        {
            var previous=Hooks.Get("StorageSync","_pendingPlace");
            string id=Wire.Meta(res)?.Id;
            if(id!=null&&Places.TryGetValue(id,out var place))Hooks.Set("StorageSync","_pendingPlace",place);
            else Hooks.Set("StorageSync","_pendingPlace",null); // native path returns the unit safely after a reload
            return previous;
        }
        public static void RestorePlacement(object old)=>Hooks.Set("StorageSync","_pendingPlace",old);
        public static void Tick()
        {
            if(SaveGameManager.Current==null||(!MPServer.IsRunning&&!MPClient.IsConnected)||Time.unscaledTime<nextAt)return;nextAt=Time.unscaledTime+3;
            foreach(var entry in Wire.Scan("pending-storage|").Where(p=>p.Value!="done"))
            {
                try
                {
                    var req=JsonConvert.DeserializeObject<StorageOpPayload>(entry.Value);
                    if(!Wire.InWorld(Wire.Meta(req)))continue;
                    req.PlayerId=MPConfig.PlayerId;Hooks.Call("StorageSync","SendOp",req);
                }
                catch(Exception ex){Hooks.Warn("storage retry: "+ex.GetBaseException().Message);}
            }
        }
        public static void Reset(){Places.Clear();current=null;nextAt=0;}
    }
    [HarmonyPatch]
    public static class StorageRequests
    {
        private static MethodBase TargetMethod()=>Hooks.Method("StorageSync","SendOp");
        private static bool Prefix(StorageOpPayload req)
        {
            if(req==null||SaveGameManager.Current==null)return false;
            StorageProgress.Sending(req);
            return MPServer.IsRunning||MPClient.IsConnected;
        }
        private static void Postfix(){StorageProgress.Commit();}
    }
    [HarmonyPatch]
    public static class CorrelatedPlacement
    {
        private static MethodBase TargetMethod()=>Hooks.Method("StorageSync","OnPlaceReduceResult");
        private static void Prefix(StorageResPayload res,out object __state)=>__state=StorageProgress.Placement(res);
        private static void Postfix(object __state)=>StorageProgress.RestorePlacement(__state);
        private static Exception Finalizer(Exception __exception,object __state){StorageProgress.RestorePlacement(__state);return __exception;}
    }
    [HarmonyPatch(typeof(MPLifecycle),"Tick")]
    public static class RetryStorage{private static void Postfix()=>StorageProgress.Tick();}
    [HarmonyPatch(typeof(MPLifecycle),"Reset")]
    public static class ResetStorage{private static void Postfix()=>StorageProgress.Reset();}
}
