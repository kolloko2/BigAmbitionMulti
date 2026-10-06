using System;
using System.Linq;
using BigAmbitionsMP;
using HarmonyLib;

namespace BAMP.SyncFix
{
    public static class HubInputs
    {
        public static bool Offer(LoanOfferPayload p)
        {
            if(p==null||string.IsNullOrEmpty(p.Id)||string.IsNullOrEmpty(p.From)||string.IsNullOrEmpty(p.To)||
                !SafetyCore.Finite(p.Principal)||p.Principal<0)return false;
            bool terminal=p.State=="accepted"||p.State=="declined"||p.State=="revoke";
            if(!terminal&&p.Principal<=0)return false;
            if(!terminal&&SaveGameManager.Current!=null&&Wire.Result("hub-offer-terminal|"+p.Id)!=null)return false;
            Wire.Ensure(p);if(!Wire.Fresh(p,"hub-offer|"+p.Id))return false;
            if(terminal&&SaveGameManager.Current!=null)Wire.Remember("hub-offer-terminal|"+p.Id,p.State);
            if(p.To==MPConfig.PlayerId&&p.State!="accepted"&&p.State!="declined"&&p.State!="revoke")
                MPHub.IncomingOffers.RemoveAll(o=>o==null||o.Id==p.Id);
            return true;
        }
        public static bool Loans(LoanStatePayload p)
        {
            if(p?.Loans==null||p.Loans.Count>10000)return false;
            var ids=new System.Collections.Generic.HashSet<string>();
            foreach(var loan in p.Loans)
                if(loan==null||string.IsNullOrEmpty(loan.Id)||!ids.Add(loan.Id)||
                    !SafetyCore.Finite(loan.Remaining)||loan.Remaining<0||
                    !SafetyCore.Finite(loan.DailyInterest)||loan.DailyInterest<0||
                    !SafetyCore.Finite(loan.DailyPayment)||loan.DailyPayment<0)return false;
            Wire.Ensure(p);return Wire.Fresh(p,"hub-loans");
        }
    }
    [HarmonyPatch(typeof(MPHub),"ReceiveOffer")]
    public static class HubOfferInput{private static bool Prefix(LoanOfferPayload p)=>HubInputs.Offer(p);}
    [HarmonyPatch(typeof(MPHub),"ApplyLoanState")]
    public static class HubLoanInput{private static bool Prefix(LoanStatePayload p)=>HubInputs.Loans(p);}
}
