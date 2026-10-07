using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;

namespace BAMP.SyncFix
{
    [HarmonyPatch]
    public static class SaveInputValidation
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return Hooks.Method("MPSaveCoordinator","HostHandleSaveData");
            yield return Hooks.Method("MPSaveCoordinator","ClientHandleStoreMirror");
        }
        private static bool Prefix(object[] __args)
        {
            object payload=__args[0];if(payload==null)return true;
            if(!(bool)payload.GetType().GetMethod("HasHsgFile").Invoke(payload,null))return true;
            try
            {
                var compressed=(byte[])payload.GetType().GetMethod("GetHsgGzip").Invoke(payload,null);
                using(var input=new MemoryStream(compressed))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var output=new MemoryStream())
                {SafetyCore.CopyBounded(gzip,output);Hooks.ValidateSave(payload,output.ToArray());}
                var world=Wire.CurrentWorld;
                var incoming=(string)payload.GetType().GetProperty("PlaythroughId")?.GetValue(payload);
                if(world!=""&&!string.IsNullOrEmpty(incoming)&&incoming!=world)throw new InvalidDataException("Save belongs to another world.");
                return true;
            }
            catch(Exception ex){Hooks.Warn("save upload/mirror refused before changing metadata: "+ex.Message);return false;}
        }
    }
    [HarmonyPatch(typeof(MPSaveManager),"MpCharacterFolder")]
    public static class RecoverInterruptedSavePair
    {
        private static void Postfix(string __result)
        {
            if(!Directory.Exists(__result))return;
            foreach(string marker in Directory.GetFiles(__result,"*.hsg.bamp-pair",SearchOption.TopDirectoryOnly))
                SafetyCore.RecoverPair(marker.Substring(0,marker.Length-".bamp-pair".Length));
        }
    }
}
