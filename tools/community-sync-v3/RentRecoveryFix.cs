using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BAMP.SyncFix
{
    public static class RentRecovery
    {
        private sealed class SaveFields : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type,MemberSerialization mode)
            {
                return type.GetFields(BindingFlags.Public|BindingFlags.Instance)
                    .Where(f=>!f.IsDefined(typeof(NonSerializedAttribute),true)&&!typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                    .Select(f=>base.CreateProperty(f,MemberSerialization.Fields)).ToList();
            }
        }
        private static readonly JsonSerializer Serializer=JsonSerializer.Create(new JsonSerializerSettings{ContractResolver=new SaveFields()});
        private static readonly string[] Fields={"AvailableForRent","RentedByPlayer","RentPerDay","lastDeposit","BusinessName","businessTypeName","Layout","itemInstances","interiorDesigns","logoSettings","scheduleDays","dirtSpots","cachedAvailableProducts"};
        public sealed class Attempt{public object Registration;public string Address,Id;public float Money;public JObject Baseline;public bool Ended;}
        [ThreadStatic] private static Attempt current;
        public static Attempt Begin(object registration,string address)
        {
            if(current!=null)throw new InvalidOperationException("Nested rental is not supported.");
            var baseline=new JObject();
            foreach(var name in Fields)
            {
                var field=registration.GetType().GetField(name);
                if(field!=null)baseline[name]=field.GetValue(registration)==null?JValue.CreateNull():JToken.FromObject(field.GetValue(registration),Serializer);
            }
            return current=new Attempt{Registration=registration,Address=address,Money=SaveGameManager.Current.Money,Baseline=baseline};
        }
        public static void Requested(string address,string id)
        {
            if(current==null||current.Address!=address)return;
            current.Id=id;Store(current);
        }
        private static void Store(Attempt attempt)
        {
            float cost=Math.Max(0,attempt.Money-SaveGameManager.Current.Money);
            Wire.Remember("rent-rollback|"+attempt.Id,new JObject{{"Cost",cost},{"Baseline",attempt.Baseline}}.ToString(Formatting.None));
        }
        public static void End(Attempt attempt)
        {
            if(attempt==null||attempt.Ended)return;attempt.Ended=true;
            try{if(attempt.Id!=null)Store(attempt);}finally{if(ReferenceEquals(current,attempt))current=null;}
        }
        public static bool Restore(string id,object registration,out float refund)
        {
            refund=0;if(id==null)return false;
            string json=Wire.Result("rent-rollback|"+id);if(json==null)return false;
            var record=JObject.Parse(json);refund=(float)record["Cost"];
            if(!SafetyCore.Finite(refund)||refund<0)throw new InvalidOperationException("Invalid rental refund.");
            // Deserialize everything before modifying the registration.
            var values=new List<Tuple<FieldInfo,object>>();
            foreach(var entry in (JObject)record["Baseline"])
            {
                var field=registration.GetType().GetField(entry.Key);
                if(field!=null)values.Add(Tuple.Create(field,entry.Value.ToObject(field.FieldType,Serializer)));
            }
            foreach(var entry in values)entry.Item1.SetValue(registration,entry.Item2);
            return true;
        }
        public static void Complete(string id){if(id!=null)JournalCache.Remove("rent-rollback|"+id);}
        public static bool Refund(string id,float amount)
        {
            if(amount==0)return true;
            var payment=new MoneyAdjustPayload{To=MPConfig.PlayerId,Amount=amount,Reason="rent-rollback refund",Silent=true};
            var stamp=Wire.Ensure(payment);if(id!=null)stamp.Id="rent-refund|"+id;
            Wire.ApplyMoney(payment);
            return Wire.Result("money|"+stamp.Id)!=null;
        }
        public static void Reset()=>current=null;
    }
    [HarmonyPatch(typeof(BizManPresentation),"RentBuilding")]
    public static class RentalCostCapture
    {
        private static void Prefix(BizManPresentation __instance,out RentRecovery.Attempt __state)
        {
            __state=null;if(MPServer.IsRunning||!MPClient.IsConnected||SaveGameManager.Current==null)return;
            var page=typeof(BizManPresentation).GetField("bizManBusiness",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(__instance);
            var registration=(BuildingRegistration)page.GetType().GetField("buildingRegistration").GetValue(page);
            if(registration==null||registration.RentedByPlayer)return;
            __state=RentRecovery.Begin(registration,GameStateReader.AddressKey(registration.Address));
        }
        private static void Postfix(RentRecovery.Attempt __state)=>RentRecovery.End(__state);
        private static Exception Finalizer(Exception __exception,RentRecovery.Attempt __state){RentRecovery.End(__state);return __exception;}
    }
    [HarmonyPatch(typeof(MPLifecycle),"Reset")]
    public static class RentalCostReset{private static void Postfix()=>RentRecovery.Reset();}
}
