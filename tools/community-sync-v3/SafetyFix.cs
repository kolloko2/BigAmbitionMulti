using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BigAmbitionsMP;
using BigAmbitions.Items;
using Entities;
using HarmonyLib;
using Helpers;
using TMPro;
using UI;
using UI.Tasks;
using UI.Smartphone.Apps.BizMan.StartBusiness;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BAMP.SyncFix
{
    public static class Hooks
    {
        public static Type Type(string name) => typeof(ModEntry).Assembly.GetType("BigAmbitionsMP." + name, true);
        public static MethodInfo Method(string type, string name) => AccessTools.Method(Type(type), name);
        public static object Call(string type, string name, params object[] args) => Method(type, name).Invoke(null, args);
        public static object Get(string type, string name) => AccessTools.Field(Type(type), name).GetValue(null);
        public static void Set(string type, string name, object value) => AccessTools.Field(Type(type), name).SetValue(null, value);
        public static void Warn(string text) => Plugin.Logger.LogWarning("[SyncFix] " + text);
        public static bool Active => MPServer.IsRunning || MPClient.IsConnected;
        public static void PreserveCargoResults(List<CargoTransferItem> ignored) { /* retain completed ACK rows */ }
        public static List<CargoTransferItem> Undelivered(List<CargoTransferItem> original, List<CargoTransferItem> completed)
        {
            return SafetyCore.RemainingAfter(original ?? new List<CargoTransferItem>(), completed?.Count ?? 0,
                item=>item.Amount>0&&!string.IsNullOrEmpty(item.ItemName));
        }
        public static void ValidateSave(object payload, byte[] bytes)
        { SafetyCore.ValidateLength(bytes, (int)payload.GetType().GetProperty("RawLength").GetValue(payload)); }
        public static void SafeTick(GameManager manager, float minutes)
        {
            if (manager == null) throw new InvalidOperationException("Catch-up manager is not ready.");
            manager.RunMainGameTick(minutes);
        }
    }

    [HarmonyPatch(typeof(MPServer), "HostMergerAction")]
    public static class WalletLeave
    {
        private static void Prefix(string action, string actorPid, out bool __state)
        { __state = action == "leave" && actorPid == MPConfig.PlayerId && MergerSync.IAmMember; }
        private static void Postfix(bool __state)
        {
            if (!__state || MergerSync.IAmMember) return;
            var cash = (IDictionary)Hooks.Get("MPServer", "CashByStableId");
            if (cash.Contains(MPConfig.StableId))
                MergerWallet.ApplyState(new MergerWalletStatePayload { GroupId = "", Balance = (float)cash[MPConfig.StableId] });
        }
    }

    [HarmonyPatch(typeof(MPServer), "HostWalletDelta")]
    public static class FiniteWallet
    {
        private static bool Prefix(string pid, float amount)
        {
            if (!SafetyCore.Finite(amount)) return false;
            var stable = (string)Hooks.Call("MPServer", "StableOfPid", pid);
            var group = MergerSync.GroupOfStable(stable);
            var balances = (IDictionary)Hooks.Get("MPServer", "_walletBalance");
            float prior = balances.Contains(group) ? (float)balances[group] : 0;
            if (!SafetyCore.Finite(prior + amount)) { Hooks.Warn("wallet overflow refused"); return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameStatePatcher), "RollbackRent")]
    public static class RentRollback
    {
        [ThreadStatic] public static string DenialId;
        private static bool Prefix(string addressKey, float lastDeposit)
        {
            string denialId=DenialId;
            GameStatePatcher.EnqueueOnMainThread(() => {
                if(denialId!=null&&Wire.Result("rent-current|"+addressKey)!=denialId)return;
                var reg = (BuildingRegistration)Hooks.Call("GameStatePatcher", "FindRegistration", addressKey);
                // Test inside the queued action: two denials queued in one frame
                // must not both refund the same optimistic rental.
                if (reg == null || SaveGameManager.Current==null) return;
                float recovered;
                bool restored=RentRecovery.Restore(denialId,reg,out recovered);
                if(!restored&&!reg.RentedByPlayer)return;
                float refund = SafetyCore.Finite(lastDeposit) && lastDeposit > 0 ? Math.Min(lastDeposit, Math.Max(0, reg.lastDeposit)) : 0;
                if(restored)refund=recovered;
                if(!restored){
                reg.RentedByPlayer = false; reg.AvailableForRent = true; reg.BusinessName = null;
                reg.businessTypeName = "ba:businesstype_empty"; reg.RentPerDay = 0; reg.lastDeposit = 0;
                if (reg.itemInstances != null)
                    foreach (var key in reg.itemInstances.Where(kv => kv.Value != null &&
                        (kv.Value.itemName == "ba:itemname_deliveryspot" || kv.Value.itemName == "ba:itemname_handtruckspawner")).Select(kv => kv.Key).ToArray())
                        reg.itemInstances.Remove(key);
                }
                try{reg.GenerateInteriorDesignerLookup();}catch(Exception ex){Hooks.Warn(ex.Message);}
                if (refund > 0 && SaveGameManager.Current != null)
                {
                    if(!RentRecovery.Refund(denialId,refund))throw new InvalidOperationException("Rental refund remains pending.");
                }
                SaveGameManager.MarkChange();
                if(denialId!=null)Wire.Remember("rent-current|"+addressKey,"closed|"+denialId);
                RentRecovery.Complete(denialId);
                try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch (Exception ex) { Hooks.Warn(ex.Message); }
                try { InstanceBehavior<UIs>.Instance?.mapFilters?.ApplyFilters(); } catch (Exception ex) { Hooks.Warn(ex.Message); }
                Hooks.Call("PassengerHud", "Toast", restored?"Rental denied; rental costs returned.":"Rental denied; deposit returned once.", 6f);
            });
            return false;
        }
    }

    [HarmonyPatch]
    public static class RentDenialIdentity
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPClient","HandleRentDeny");
        private static bool Prefix(MessageEnvelope env,out string __state)
        {
            __state=RentRollback.DenialId;
            var p=env.GetPayload<BuildingOwnershipPayload>();var stamp=Wire.Meta(p);
            if(p==null||SaveGameManager.Current==null||!Wire.InWorld(stamp)||Wire.Result("rent-current|"+p.AddressKey)!=stamp.Id)return false;
            RentRollback.DenialId=stamp.Id;return true;
        }
        private static void Postfix(string __state)=>RentRollback.DenialId=__state;
        private static Exception Finalizer(Exception __exception,string __state){RentRollback.DenialId=__state;return __exception;}
    }

    [HarmonyPatch(typeof(TasksUI), "InstantlyCompleteListOfTasks")]
    public static class CompleteTasks
    {
        private static bool Prefix(TasksUI __instance, IEnumerable<TodoTask> tasks)
        {
            if (!Hooks.Active) return true;
            var todo = SaveGameManager.Current?.TodoTasks;
            if (todo == null || tasks == null) return false;
            var ids = new HashSet<string>(tasks.Where(t => t != null && !string.IsNullOrEmpty(t.id)).Select(t => t.id));
            var group = (Transform)AccessTools.Field(typeof(TasksUI), "_tasksGroup").GetValue(__instance);
            if (group != null)
                foreach (var id in ids) { var row = group.Find(id); if (row != null) Object.Destroy(row.gameObject); }
            todo.RemoveAll(t => t != null && ids.Contains(t.id));
            SaveGameManager.MarkChange();
            AccessTools.Method(typeof(TasksUI), "ScheduleUpdateObjectivesHeight").Invoke(__instance, null);
            return false;
        }
    }

    [HarmonyPatch(typeof(StartBusinessUI), "SetUpBusiness")]
    public static class StartBusinessRefs
    {
        private static bool Prefix(StartBusinessUI __instance)
        {
            if (!Hooks.Active) return true;
            var registrationField = AccessTools.Field(typeof(StartBusinessUI), "_buildingRegistration");
            var reg = (BuildingRegistration)registrationField.GetValue(__instance);
            if (reg == null)
            {
                var page = __instance.GetComponentInParent<BizManBusiness>();
                reg = page?.buildingRegistration;
                if (reg != null) registrationField.SetValue(__instance, reg);
            }
            var nameField = AccessTools.Field(typeof(StartBusinessUI), "businessNameField");
            if ((TMP_InputField)nameField.GetValue(__instance) == null)
            {
                var fields = __instance.GetComponentsInChildren<TMP_InputField>(true);
                if (fields.Length == 1) nameField.SetValue(__instance, fields[0]);
            }
            if (reg == null || (TMP_InputField)nameField.GetValue(__instance) == null || SaveGameManager.Current == null)
            { Hooks.Warn("start business refused: page data not ready; no business mutation made"); return false; }
            var selectedField=AccessTools.Field(typeof(StartBusinessUI),"_selectedType");
            string selected=(string)selectedField.GetValue(__instance);
            if(string.IsNullOrEmpty(selected)||selected=="ba:businesstype_empty"||BusinessTypeHelper.GetData(selected)==null)
            {
                var choices=new List<string>();
                var rows=(IDictionary)AccessTools.Field(typeof(StartBusinessUI),"_businessTypes").GetValue(__instance);
                foreach(DictionaryEntry entry in rows)
                {
                    if(entry.Value==null)continue;
                    var outline=(GameObject)AccessTools.Field(entry.Value.GetType(),"selectedOutline").GetValue(entry.Value);
                    if(outline!=null&&outline.activeSelf&&BusinessTypeHelper.GetData((string)entry.Key)!=null)choices.Add((string)entry.Key);
                }
                if(choices.Count==1)selectedField.SetValue(__instance,choices[0]);
                else {Hooks.Call("PassengerHud","Toast","Select a business type before opening the business.",6f);return false;}
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(TimeSync), "TickClockCorrection")]
    public static class ClockCorrection
    {
        private static bool Prefix()
        {
            float hours = (float)Hooks.Get("TimeSync", "_correctionHours");
            if (!Hooks.Active || !GameStateReader.HasLiveWorld() || hours <= 0 || Time.timeScale == 0) return false;
            var manager = InstanceBehavior<GameManager>.Instance;
            if (manager == null) return false;
            float minutes = Math.Min(Math.Min(MPRestSync.SkipMinutesPerRealSecond * Time.unscaledDeltaTime, 60), hours * 60);
            if (minutes <= 0 || !SafetyCore.Finite(minutes)) return false;
            var current = SaveGameManager.Current;
            double before = current.Day * 1440d + current.Hour * 60d + current.Minute;
            try { manager.RunMainGameTick(minutes); }
            catch (Exception ex)
            {
                // Never replay an economic tick that may have executed partially.
                Hooks.Set("TimeSync", "_correctionHours", 0f);
                Hooks.Warn("catch-up stopped after failed tick; waiting for authoritative clock: " + ex.Message);
                return false;
            }
            double after = current.Day * 1440d + current.Hour * 60d + current.Minute;
            float advanced = (float)Math.Max(0, after - before);
            if (advanced > 0)
            {
                Hooks.Set("TimeSync", "_wroteClock", true);
                Hooks.Set("TimeSync", "_correctionHours", Math.Max(0, hours - advanced / 60));
            }
            return false;
        }
    }
}
