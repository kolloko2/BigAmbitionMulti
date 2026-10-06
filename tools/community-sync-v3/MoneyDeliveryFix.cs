using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace BAMP.SyncFix
{
    public sealed class MoneyReceipt { public string PlayerId, Id; public WireStamp FixStamp; }
    public sealed class MoneyDelivery { public string Stable, Group; public MoneyAdjustPayload Payload; public bool Acked, CashSettled; public long AckSequence; public string AckEpoch; }
    public static class MoneyTransport
    {
        public const int ReceiptType = 223;
        [ThreadStatic] public static string Context;
        [ThreadStatic] public static MoneyAdjustPayload Applying;
        [ThreadStatic] public static LoanOfferPayload CurrentOffer;
        private static float nextAt;
        private static readonly HashSet<string> SeenOnline = new HashSet<string>();
        public static void ReturnPendingOffer(LoanOfferPayload offer)
        {
            // AnswerOffer removes the local inbox row before host validation.
            // Restore it with a fresh host stamp when funds remain unavailable.
            var copy=JsonConvert.DeserializeObject<LoanOfferPayload>(JsonConvert.SerializeObject(offer));
            Wire.Assign(copy,Wire.New());
            if(copy.To==MPConfig.PlayerId)MPHub.ReceiveOffer(copy);
            else MPServer.SendHubTo(copy.To,MessageType.LoanOffer,copy);
        }
        public static float NativeAvailable(string pid)
        {
            string stable=(string)Hooks.Call("MPServer","StableOfPid",pid),group=MergerSync.GroupOfStable(stable);
            if(!string.IsNullOrEmpty(group)&&MPServer.TryWalletBalance(group,out var balance))return SafetyCore.Finite(balance)?balance:0;
            return pid==MPConfig.PlayerId?SaveGameManager.Current?.Money??0:MPServer.GetKnownCash(pid);
        }
        public static float NativeLocalAvailable()=>NativeAvailable(MPConfig.PlayerId);
        public static MoneyDelivery Prepare(string pid,float amount,string reason,bool silent)
        {
            if(!SafetyCore.Finite(amount)||SaveGameManager.Current==null)throw new InvalidOperationException("Money intent requires a valid amount and loaded character.");
            string stable=(string)Hooks.Call("MPServer","StableOfPid",pid);
            if(string.IsNullOrEmpty(stable))throw new InvalidOperationException("Cannot deliver money to an unidentified character.");
            string id=string.IsNullOrEmpty(Context)?Guid.NewGuid().ToString("N"):Context+"|"+stable+"|"+amount.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
            string key="money-out|"+id;
            var existing=Wire.Result(key)??Wire.Result("money-closed|"+id);
            var delivery=existing==null?new MoneyDelivery {Stable=stable,Group=MergerSync.GroupOfStable(stable),Payload=new MoneyAdjustPayload {To=pid,Amount=amount,Reason=reason,Silent=silent}}:JsonConvert.DeserializeObject<MoneyDelivery>(existing);
            if(existing==null)
            {
                var stamp=Wire.Ensure(delivery.Payload);stamp.Id=id;
                Wire.Remember(key,JsonConvert.SerializeObject(delivery));
            }
            return delivery;
        }
        public static void Deliver(string pid, float amount, string reason, bool silent)
        {
            if(CurrentOffer!=null&&Context=="offer|"+CurrentOffer.Id)
            {
                PreparePair(CurrentOffer.From,-CurrentOffer.Principal,CurrentOffer.To,CurrentOffer.Principal,reason);
            }
            var delivery=Prepare(pid,amount,reason,silent);
            string key="money-out|"+Wire.Meta(delivery.Payload).Id;
            delivery.Payload.To=pid;
            if(pid==MPConfig.PlayerId)
            {
                Wire.ApplyMoney(delivery.Payload);
                if(Wire.Result("money|"+Wire.Meta(delivery.Payload).Id)==null)throw new InvalidOperationException("Local money effect was refused; delivery remains pending.");
                delivery.Acked=true;delivery.CashSettled=true;Wire.Remember(key,JsonConvert.SerializeObject(delivery));CloseSettled();
            }
            else MPServer.SendHubTo(pid,MessageType.MoneyAdjust,delivery.Payload);
        }
        public static void PreparePair(string first,float firstAmount,string second,float secondAmount,string reason)
        {
            // Validate both destinations before registering either delivery.
            if(!SafetyCore.Finite(firstAmount)||!SafetyCore.Finite(secondAmount)||
               string.IsNullOrEmpty((string)Hooks.Call("MPServer","StableOfPid",first))||
               string.IsNullOrEmpty((string)Hooks.Call("MPServer","StableOfPid",second)))
                throw new InvalidOperationException("Both money destinations must be identified before preparing a transfer.");
            Prepare(first,firstAmount,reason,true);Prepare(second,secondAmount,reason,true);
        }
        public static void Receipt(MoneyAdjustPayload payload)
        {
            if(MPServer.IsRunning)return;
            var id=Wire.Meta(payload)?.Id;if(string.IsNullOrEmpty(id))return;
            MPClient.SendEnvelope(MessageEnvelope.Create((MessageType)ReceiptType,MPConfig.PlayerId,new MoneyReceipt {PlayerId=MPConfig.PlayerId,Id=id}));
        }
        public static bool Consume(MessageEnvelope envelope,string sender)
        {
            if(envelope.Type==MessageType.CashSync)
            {
                var cash=envelope.GetPayload<CashSyncPayload>();
                if(cash==null||cash.PlayerId!=sender||!SafetyCore.Finite(cash.Money)||!Wire.Fresh(cash,"cash|"+sender))return true;
                var stamp=Wire.Meta(cash);
                GameStatePatcher.EnqueueOnMainThread(()=>{
                    var stable=(string)Hooks.Call("MPServer","StableOfPid",sender);
                    foreach(var pair in Wire.Scan("money-out|"))
                    {
                        var record=JsonConvert.DeserializeObject<MoneyDelivery>(pair.Value);
                        if(record.Stable==stable&&record.Acked&&!record.CashSettled&&record.AckEpoch==stamp.Epoch&&stamp.Sequence>record.AckSequence)
                        {record.CashSettled=true;Wire.Remember(pair.Key,JsonConvert.SerializeObject(record));}
                    }
                    CloseSettled();
                });
                return false;
            }
            if((int)envelope.Type!=ReceiptType)return false;
            var receipt=envelope.GetPayload<MoneyReceipt>();
            if(receipt==null||string.IsNullOrEmpty(sender)||receipt.PlayerId!=sender||!Wire.InWorld(receipt.FixStamp))return true;
            GameStatePatcher.EnqueueOnMainThread(()=>{
                string key="money-out|"+receipt.Id;var json=Wire.Result(key);if(json==null)return;
                var delivery=JsonConvert.DeserializeObject<MoneyDelivery>(json);
                var stable=(string)Hooks.Call("MPServer","StableOfPid",sender);
                if(stable!=delivery.Stable)return;
                delivery.Acked=true;delivery.AckSequence=receipt.FixStamp.Sequence;delivery.AckEpoch=receipt.FixStamp.Epoch;Wire.Remember(key,JsonConvert.SerializeObject(delivery));CloseSettled();
            });return true;
        }
        public static float Reserved(string pid)
        {
            if(SaveGameManager.Current==null)return 0;
            var stable=(string)Hooks.Call("MPServer","StableOfPid",pid);
            string group=MergerSync.GroupOfStable(stable);float reserved=0;
            foreach(var pair in Wire.Scan("money-out|"))
            {
                var record=JsonConvert.DeserializeObject<MoneyDelivery>(pair.Value);
                bool settled=record.Group!="" ? Wire.Result("wallet|money-linked|"+Wire.Meta(record.Payload).Id)!=null : record.CashSettled;
                if(!settled&&record.Payload.Amount<0&&(record.Stable==stable||(group!=""&&record.Group==group)))reserved-=record.Payload.Amount;
            }
            return reserved;
        }
        public static void Tick()
        {
            if(!MPServer.IsRunning||SaveGameManager.Current==null||Time.unscaledTime<nextAt)return;nextAt=Time.unscaledTime+2;
            var online=new HashSet<string>(MPRestSync.AllPlayers());
            var newly=online.Where(p=>!SeenOnline.Contains(p)).ToArray();SeenOnline.IntersectWith(online);SeenOnline.UnionWith(online);
            CloseSettled();
            var retry=Wire.Scan("money-out|").AsEnumerable();
            if(newly.Length>0)retry=retry.Concat(Wire.Scan("money-closed|"));
            foreach(var pair in retry)
            {
                try{
                var delivery=JsonConvert.DeserializeObject<MoneyDelivery>(pair.Value);
                if(delivery?.Payload==null||!Wire.InWorld(Wire.Meta(delivery.Payload)))continue;
                string pid=(string)Hooks.Call("MPServer","PidOfStable",delivery.Stable);
                if(string.IsNullOrEmpty(pid)||pid==MPConfig.PlayerId||!online.Contains(pid))continue;
                if(delivery.Acked&&!newly.Contains(pid))continue;
                delivery.Payload.To=pid;
                MPServer.SendHubTo(pid,MessageType.MoneyAdjust,delivery.Payload);
                }catch(Exception ex){Hooks.Warn("money retry "+pair.Key+": "+ex.GetBaseException().Message);}
            }
        }
        public static void CloseSettled()
        {
            foreach(var pair in Wire.Scan("money-out|"))
            {
                try{
                var record=JsonConvert.DeserializeObject<MoneyDelivery>(pair.Value);
                bool settled=record.Group!=""?Wire.Result("wallet|money-linked|"+Wire.Meta(record.Payload).Id)!=null:record.CashSettled;
                if(!record.Acked||!settled)continue;
                Wire.Remember("money-closed|"+Wire.Meta(record.Payload).Id,pair.Value);JournalCache.Remove(pair.Key);
                }catch(Exception ex){Hooks.Warn("money settlement "+pair.Key+": "+ex.GetBaseException().Message);}
            }
        }
        public static void Reset(){SeenOnline.Clear();nextAt=0;Context=null;Applying=null;CurrentOffer=null;}
    }
    [HarmonyPatch]
    public static class ReliableMoneyDelivery
    {
        private static MethodBase TargetMethod()=>Hooks.Method("MPHub","DeliverMoney");
        private static bool Prefix(string playerId,float amount,string reason,bool silent)
        {MoneyTransport.Deliver(playerId,amount,reason,silent);return false;}
    }
    [HarmonyPatch(typeof(MPHub),"HostHandleAnswer")]
    public static class OfferFundsReservation
    {
        private static bool Prefix(LoanAnswerPayload a,out string __state)
        {
            __state=MoneyTransport.Context;MoneyTransport.Context=a==null?null:"offer|"+a.Id;
            if(a==null||!a.Accept)return true;
            var offers=(IDictionary)Hooks.Get("MPHub","_hostOffers");if(!offers.Contains(a.Id))return true;
            var offer=(LoanOfferPayload)offers[a.Id];
            CurrentOfferCapture(offer);
            if(!SafetyCore.Finite(offer.Principal)||offer.Principal<=0)return false;
            float cash=offer.From==MPConfig.PlayerId?SaveGameManager.Current?.Money??-1:MPServer.GetKnownCash(offer.From);
            string stable=(string)Hooks.Call("MPServer","StableOfPid",offer.From), group=MergerSync.GroupOfStable(stable);
            if(group!=""&&MPServer.TryWalletBalance(group,out var balance))cash=balance;
            if(cash<0||!SafetyCore.Finite(cash)||cash-MoneyTransport.Reserved(offer.From)<offer.Principal)
            {Hooks.Warn("offer acceptance deferred: unknown or reserved funds; offer remains pending");MoneyTransport.ReturnPendingOffer(offer);return false;}
            return true;
        }
        private static void CurrentOfferCapture(LoanOfferPayload offer){MoneyTransport.CurrentOffer=offer;}
        private static void Postfix(string __state){MoneyTransport.Context=__state;MoneyTransport.CurrentOffer=null;}
        private static Exception Finalizer(Exception __exception,string __state){MoneyTransport.Context=__state;MoneyTransport.CurrentOffer=null;return __exception;}
    }
    [HarmonyPatch(typeof(MPLifecycle),"Tick")]
    public static class RetryMoney {private static void Postfix()=>MoneyTransport.Tick();}
    [HarmonyPatch(typeof(MPLifecycle),"Reset")]
    public static class ResetMoney {private static void Postfix()=>MoneyTransport.Reset();}

    [HarmonyPatch(typeof(MPHub),"HostHandleRepay")]
    public static class RepaymentSafety
    {
        public sealed class State {public string OldContext,Key;public LoanEntry Loan;public float Remaining;}
        private static bool Prefix(LoanRepayPayload p,out State __state)
        {
            __state=new State {OldContext=MoneyTransport.Context};
            if(p==null||!SafetyCore.Finite(p.Amount)||SaveGameManager.Current==null)return false;
            var stamp=Wire.Ensure(p);if(!Wire.InWorld(stamp))return false;
            __state.Key="repay-request|"+stamp.Id;
            if(Wire.Result(__state.Key)!=null)return false;
            var loan=((IEnumerable)Hooks.Get("MPHub","_hostLoans")).Cast<LoanEntry>().FirstOrDefault(l=>l.Id==p.Id&&l.Borrower==p.From);
            if(loan==null)return false;
            float amount=p.Amount<=0?loan.Remaining:Math.Min(p.Amount,loan.Remaining);
            string stable=(string)Hooks.Call("MPServer","StableOfPid",p.From),group=MergerSync.GroupOfStable(stable);
            float cash=p.From==MPConfig.PlayerId?SaveGameManager.Current.Money:MPServer.GetKnownCash(p.From);
            if(group!=""&&MPServer.TryWalletBalance(group,out var balance))cash=balance;
            if(!SafetyCore.Finite(amount)||amount<=0||!SafetyCore.Finite(cash)||cash-MoneyTransport.Reserved(p.From)<amount)return false;
            if(!MPRestSync.AllPlayers().Contains(loan.Borrower)||!MPRestSync.AllPlayers().Contains(loan.Lender))return true;
            __state.Loan=loan;__state.Remaining=loan.Remaining;MoneyTransport.Context="repay|"+stamp.Id;
            MoneyTransport.PreparePair(loan.Borrower,-amount,loan.Lender,amount,"early repayment");return true;
        }
        private static void Postfix(State __state)
        {
            MoneyTransport.Context=__state.OldContext;
            if(__state.Loan!=null&&__state.Loan.Remaining<__state.Remaining)Wire.Remember(__state.Key,"completed");
        }
        private static Exception Finalizer(Exception __exception,State __state)
        {
            if(__state!=null)
            {
                MoneyTransport.Context=__state.OldContext;
                if(__state.Loan!=null&&__state.Loan.Remaining<__state.Remaining)Wire.Remember(__state.Key,"completed");
            }
            return __exception;
        }
    }
}
