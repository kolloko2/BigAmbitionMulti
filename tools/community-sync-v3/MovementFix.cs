using System;
using System.Collections;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using UnityEngine;

namespace BAMP.SyncFix
{
    [HarmonyPatch]
    public static class PlayerRouteFreshness
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPServer","HandlePlayerMove");
        private static bool Prefix(string senderPid,MessageEnvelope env)
        {
            var p=env.GetPayload<PlayerPositionPayload>();
            return p!=null&&p.PlayerId==senderPid&&Wire.Fresh(p,"player-route|"+senderPid);
        }
    }
    [HarmonyPatch]
    public static class DriveLease
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPServer","HandleVehicleDrive");
        private static bool Prefix(string senderPid,MessageEnvelope env)
        {
            var p=env.GetPayload<VehicleDrivePayload>();
            if(p==null||p.DriverId!=senderPid||!Wire.Fresh(p,"driver-route|"+p.VehicleId))return false;
            return Allowed(p,senderPid);
        }
        public static bool Allowed(VehicleDrivePayload p,string senderPid)
        {
            var driven=(IEnumerable)Hooks.Get("MPServer","_drivenCars");
            foreach(var entry in driven)
            {
                var type=entry.GetType();if((string)type.GetProperty("Key").GetValue(entry)!=p.VehicleId)continue;
                var record=type.GetProperty("Value").GetValue(entry);
                string owner=(string)AccessTools.Field(record.GetType(),"Driver").GetValue(record);
                int at=(int)AccessTools.Field(record.GetType(),"TickMs").GetValue(record);
                if(owner!=senderPid&&(p.Released||unchecked(Environment.TickCount-at)<5000))
                {Hooks.Warn("concurrent driver refused for "+p.VehicleId);return false;}
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(MPServer),"BroadcastVehicleDrive")]
    public static class HostDriveLease
    {
        private static bool Prefix(VehicleDrivePayload p)
        {return p!=null&&DriveLease.Allowed(p,MPConfig.PlayerId);}
    }
    [HarmonyPatch(typeof(RemotePlayerMover),"SetTarget")]
    public static class FiniteMovement
    {
        private static bool Prefix(Vector3 pos,Quaternion rot,float senderT)=>SafetyCore.Finite(senderT)&&
            SafetyCore.Finite(pos.x)&&SafetyCore.Finite(pos.y)&&SafetyCore.Finite(pos.z)&&
            SafetyCore.Finite(rot.x)&&SafetyCore.Finite(rot.y)&&SafetyCore.Finite(rot.z)&&SafetyCore.Finite(rot.w);
    }
}
