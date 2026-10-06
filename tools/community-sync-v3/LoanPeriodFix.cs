using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json;

namespace BAMP.SyncFix
{
    public sealed class LoanPeriod {public int Day;public string Id,Lender,Borrower;public float Before,Principal,Charge;public bool Completed;}
    public static class LoanPeriods
    {
        private static float next;
        public static void Tick()
        {
            if(!MPServer.IsRunning||SaveGameManager.Current==null||UnityEngine.Time.unscaledTime<next)return;next=UnityEngine.Time.unscaledTime+1;
            Hooks.Call("MPHub","TryLoadLedger");
            int day=GameStateReader.GetGameTime().day;if(day<=0)return;
            int last=(int)Hooks.Get("MPHub","_lastDay");
            string cursor=Wire.Result("loan-period-cursor");
            if(cursor!=null&&int.TryParse(cursor,out var saved))last=saved;
            if(last<0){Hooks.Set("MPHub","_lastDay",day);Wire.Remember("loan-period-cursor",day.ToString());return;}
            if(day<last){Hooks.Warn("loan clock moved backwards; no extra payment charged");return;}
            if(day==last)return;
            var loans=(List<LoanEntry>)Hooks.Get("MPHub","_hostLoans");
            var online=new HashSet<string>(MPRestSync.AllPlayers());
            for(int period=last+1;period<=day;period++)
            {
                foreach(var loan in loans.ToArray())
                {
                    string key="loan-day|"+loan.Id+"|"+period;
                    var json=Wire.Result(key);
                    var op=json==null?null:JsonConvert.DeserializeObject<LoanPeriod>(json);
                    if(op?.Completed==true)
                    {
                        // Resuming a partially completed day can reload an older
                        // loan ledger. Do not leave paid principal outstanding,
                        // and do not undo a later early repayment.
                        loan.Remaining=Math.Min(loan.Remaining,Math.Max(0,op.Before-op.Principal));
                        if(loan.Remaining<=0)loans.Remove(loan);
                        continue;
                    }
                    if(op==null)
                    {
                        if(!online.Contains(loan.Borrower)||!online.Contains(loan.Lender)||loan.Remaining<=0)continue; // original offline-day policy
                        float principal=Math.Min(loan.DailyPayment,loan.Remaining),charge=loan.DailyInterest+principal;
                        if(!SafetyCore.Finite(principal)||principal<0||!SafetyCore.Finite(charge)||charge<=0){Hooks.Warn("invalid loan terms refused for "+loan.Id);continue;}
                        op=new LoanPeriod {Day=period,Id=loan.Id,Lender=loan.Lender,Borrower=loan.Borrower,Before=loan.Remaining,Principal=principal,Charge=charge};
                        Wire.Remember(key,JsonConvert.SerializeObject(op));
                    }
                    // Both legs are recorded before either is sent. A retry uses
                    // the same per-loan/day identity and the stored original quote.
                    var old=MoneyTransport.Context;MoneyTransport.Context="loan-day|"+loan.Id+"|"+period;
                    try
                    {
                        MoneyTransport.PreparePair(op.Borrower,-op.Charge,op.Lender,op.Charge,"daily loan payment");
                        MoneyTransport.Deliver(op.Borrower,-op.Charge,"loan payment to "+op.Lender,true);
                        MoneyTransport.Deliver(op.Lender,op.Charge,"loan payment from "+op.Borrower,true);
                        loan.Remaining=Math.Max(0,op.Before-op.Principal);op.Completed=true;Wire.Remember(key,JsonConvert.SerializeObject(op));
                    }
                    finally{MoneyTransport.Context=old;}
                    if(loan.Remaining<=0)loans.Remove(loan);
                }
                // A failure above exits without advancing this cursor. Completed
                // loan records let the next attempt finish just the remaining ones.
                Wire.Remember("loan-period-cursor",period.ToString());Hooks.Set("MPHub","_lastDay",period);
            }
            Hooks.Call("MPHub","HostBroadcastLoans");MPHub.SaveLedger();
        }
        public static void Reset(){next=0;}
    }
    [HarmonyPatch(typeof(MPHub),"HostTick")]
    public static class LoanDayRecovery
    {
        private static bool Prefix(){try{LoanPeriods.Tick();}catch(Exception ex){Hooks.Warn("loan period pending: "+ex.GetBaseException().Message);}return false;}
    }
    [HarmonyPatch(typeof(MPLifecycle),"Reset")]
    public static class LoanDayReset{private static void Postfix()=>LoanPeriods.Reset();}
}
