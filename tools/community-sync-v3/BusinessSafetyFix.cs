using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigAmbitionsMP;
using BigAmbitions.Items;
using HarmonyLib;
using Helpers;
using UI.Smartphone.Apps.BizMan.StartBusiness;
using UnityEngine;

namespace BAMP.SyncFix
{
    public static class BusinessHooks
    {
        public static BuildingRegistration IndoorRegistration(BuildingManager manager) => manager == null ? null : manager.buildingRegistration;
        public static void Adjust(float amount, string reason)
        {
            if (!SafetyCore.Finite(amount) || amount == 0 || SaveGameManager.Current == null) return;
            SaveGameManager.Current.Money += amount;
            MergerWallet.ForwardExternal(amount, reason);
            Hooks.Call("DesignerBalanceKeeper", "OnExternalMoneyChange", amount, reason);
            SaveGameManager.MarkChange();
        }
    }
    [HarmonyPatch(typeof(StartBusinessUI), "SetUpBusiness")]
    public static class StartOutsideBuilding
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int matches=0;
            foreach(var instruction in instructions)
            {
                if(instruction.opcode==OpCodes.Ldfld && instruction.operand is FieldInfo f && f.DeclaringType==typeof(BuildingManager) && f.Name=="buildingRegistration")
                {instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(BusinessHooks),"IndoorRegistration");matches++;}
                yield return instruction;
            }
            if(matches!=1)throw new InvalidOperationException("StartBusiness indoor-refresh hook did not match this game build.");
        }
    }

    [HarmonyPatch(typeof(BizManPresentation), "OnTerminateContractConfirm")]
    public static class TerminatePageRefs
    {
        private static bool Prefix(BizManPresentation __instance)
        {
            if(!Hooks.Active)return true;
            var field=AccessTools.Field(typeof(BizManPresentation),"bizManBusiness");
            var page=(BizManBusiness)field.GetValue(__instance);
            if(page==null) {page=__instance.GetComponentInParent<BizManBusiness>();if(page!=null)field.SetValue(__instance,page);}
            if(page==null||!BAMP.HostBizManFix.Recovery.EnsureData(page)) {Hooks.Warn("terminate refused: business page not ready");return false;}
            if(!page.buildingRegistration.RentedByPlayer) return false;
            if(page.buildingRegistration.itemInstances==null||SaveGameManager.Current?.VehicleInstances==null)
            {Hooks.Warn("terminate refused: building inventory not loaded");return false;}
            return true;
        }
    }

    [HarmonyPatch]
    public static class RoutedFinancialTransaction
    {
        public sealed class State
        {
            public string Key; public float Money; public int Campaigns; public float SaleTotal;
            public readonly Dictionary<ItemInstance,List<CargoInstance>> Cargo=new Dictionary<ItemInstance,List<CargoInstance>>();
            public string Method;
        }
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return Hooks.Method("SharedShopWorkTabs","ApplyRoutedSellAll");
            yield return Hooks.Method("SharedShopWorkTabs","ApplyRoutedCampaignCreate");
        }
        private static bool Prefix(BuildingRegistration reg, SharedWorkEditPayload p, MethodBase __originalMethod, out State __state)
        {
            __state=null;
            if(reg==null||p==null||SaveGameManager.Current==null)return true;
            var stamp=Wire.Ensure(p);
            if(!Wire.InWorld(stamp))return false;
            string key="routed-finance|"+stamp.Id;
            if(Wire.Result(key)!=null)return false;
            __state=new State {Key=key,Money=SaveGameManager.Current.Money,Campaigns=SaveGameManager.Current.RecruitmentCampaigns?.Count??0,Method=__originalMethod.Name};
            if(__state.Method=="ApplyRoutedSellAll")
            {
                var items=new List<ItemInstance>();
                __state.SaleTotal=(float)Hooks.Call("SharedShopWorkTabs","SellAllTotal",reg,items);
                foreach(var item in items) if(item?.cargoInstances!=null) __state.Cargo[item]=item.cargoInstances.ToList();
            }
            return true;
        }
        private static void Postfix(State __state)
        {
            if(__state==null||SaveGameManager.Current==null)return;
            var current=SaveGameManager.Current;
            if(__state.Method=="ApplyRoutedCampaignCreate")
            {
                if((current.RecruitmentCampaigns?.Count??0)>__state.Campaigns) {Wire.Remember(__state.Key,"completed");return;}
                float delta=current.Money-__state.Money;
                if(delta<0)BusinessHooks.Adjust(-delta,"recruitment rollback");
                Wire.Remember(__state.Key,"refused-or-rolled-back");
            }
            else
            {
                bool empty=__state.Cargo.All(kv=>kv.Key.cargoInstances!=null&&kv.Key.cargoInstances.Count==0);
                float delta=current.Money-__state.Money;
                if(empty&&Math.Abs(delta-__state.SaleTotal)<0.01f) {Wire.Remember(__state.Key,"completed");return;}
                // A stale quote refuses before mutation; this restoration also
                // handles an exception after the first shelf was cleared.
                foreach(var pair in __state.Cargo)
                {pair.Key.cargoInstances=pair.Value;try{pair.Key.OnItemsInCargoUpdated()?.Invoke();}catch(Exception ex){Hooks.Warn(ex.Message);}}
                if(delta!=0)BusinessHooks.Adjust(-delta,"inventory sale rollback");
                Wire.Remember(__state.Key,"refused-or-rolled-back");
            }
        }
    }

    [HarmonyPatch]
    public static class WorkCommandOrdering
    {
        private static MethodBase TargetMethod()=>Hooks.Method("SharedShopWorkTabs","OwnerApplyEdit");
        private static bool Prefix(SharedWorkEditPayload p, out string __state)
        {
            __state=null;if(p==null)return true;
            // A refusal is an answer for the sender's UI, not an edit of a
            // registration on this machine. Native code handles it first.
            if(p.Op=="mergerplanedit"&&p.PlanOp=="refused")return true;
            if(string.IsNullOrEmpty(p.AddressKey)||SaveGameManager.Current==null||Hooks.Call("GameStatePatcher","FindRegistration",p.AddressKey)==null)return false;
            Wire.Ensure(p);var stamp=Wire.Meta(p);
            if(!Wire.InWorld(stamp))return false;
            string key="work|"+stamp.Id;
            if(Wire.Result(key)!=null)return false;
            // These are independent commands (e.g. end two contracts), not
            // replaceable snapshots. A later command cannot supersede an earlier ID.
            __state=key;
            return true;
        }
        private static void Postfix(string __state,bool __runOriginal) {if(__runOriginal&&__state!=null&&Wire.Result(__state)==null)Wire.Remember(__state,"processed");}
    }
}
