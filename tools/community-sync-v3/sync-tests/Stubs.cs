using System.Collections;
using System.Reflection;
using BAMP.SyncFix;
using Newtonsoft.Json;
namespace HarmonyLib {
 public static class AccessTools{public static FieldInfo Field(Type t,string n)=>t.GetField(n,BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);}
 [AttributeUsage(AttributeTargets.Class|AttributeTargets.Method,AllowMultiple=true)]public class HarmonyPatch:Attribute {public HarmonyPatch(){}public HarmonyPatch(Type t,string s){}}
}
namespace UnityEngine {public class Object{}public static class Time{public static float unscaledTime;}}
public class BizManPresentation{}
public class BuildingRegistration {public bool AvailableForRent=true,RentedByPlayer;public float RentPerDay,lastDeposit;public string BusinessName,businessTypeName="empty",Layout="original";public string Address="shop";public Dictionary<string,RentalItem> itemInstances=new();}
public class RentalItem {public string Name;public int Count;[NonSerialized]public UnityEngine.Object Cache;public string UnsafeProperty=>throw new Exception("runtime property must not be serialized");}
public class GameInstance {public float Money=1000;public Dictionary<string,string> modData=new();}
public static class SaveGameManager {public static GameInstance Current=new();public static void MarkChange(){}}
namespace BAMP.SyncFix {
 public static class Hooks {
  public static bool Active=true;public static MethodBase Method(string t,string n)=>typeof(BigAmbitionsMP.MPServer).GetMethod(n);
  public static Dictionary<string,Func<object[],object>> Calls=new();
  public static object Get(string t,string n)=>BigAmbitionsMP.MPServer.Fields[n];
  public static object Call(string t,string n,params object[] args)=>Calls.TryGetValue(t+"."+n,out var call)?call(args):n switch {"StableOfPid"=>args[0],"PidOfStable"=>args[0],_=>null};
  public static void Set(string t,string n,object value)=>BigAmbitionsMP.MPServer.Fields[n]=value;
  public static void Warn(string s){}
 }
}
namespace BigAmbitionsMP {
 public enum MessageType{MoneyAdjust=1,MergerWalletDelta=2,CashSync=3,RentRequest=4,InteriorSnapshot=5,InteriorCargoSync=6,BuildingInteriorDelta=7,LoanOffer=8}
 public class InteriorSnapshotPayload:Payload{public string AddressKey{get;set;}}
 public class InteriorCargoSyncPayload:Payload{public string AddressKey{get;set;}}
 public class InteriorEditDeltaPayload:Payload{public string AddressKey{get;set;}}
 public class BuildingOwnershipPayload:Payload{public string AddressKey;}
 public class MessageEnvelope {
  public MessageType Type;public string Data,SenderId;public byte[] Attachment;
  public static MessageEnvelope Create<T>(MessageType t,string sender,T p){var e=new MessageEnvelope{Type=t};Wire.StampEnvelope(e,p);return e;}
  public T GetPayload<T>()=>JsonConvert.DeserializeObject<T>(Data);
 }
 public class Payload{public WireStamp FixStamp;}
 public class MoneyAdjustPayload:Payload{public string To,Reason;public float Amount;public bool Silent;}
 public class CashSyncPayload:Payload{public string PlayerId;public float Money;}
 public class MergerWalletDeltaPayload:Payload{public string PlayerId,Key;public float Amount;public bool Contribution;public PwTransaction Tx;}
 public class PwTransaction{}
 public class MergerWalletStatePayload:Payload{public string GroupId;public float Balance;}
 public class MergerGroupInfo:Payload{public List<string> MemberPids=new();}
 public class StorageOpPayload:Payload{public string Ctx,PlayerId,AddressKey,Container,VehicleId,ItemId;}
 public class StorageResPayload:Payload{public string Reason,AddressKey;public bool Ok;}
 public class CustomerSimAuthorityPayload:Payload{public string AddressKey{get;set;}public string SimulatorPid{get;set;}}
 public class CustomerPuppetStatePayload:Payload{public string AddressKey{get;set;}public string SimulatorPid{get;set;}}
 public class CustomerVisitStatePayload:Payload{public string AddressKey{get;set;}public string SimulatorPid{get;set;}public bool Final;}
 public class PlayerPositionPayload:Payload{public string PlayerId;}
 public class LoanOfferPayload:Payload{public string Id,From,To,State="offer";public float Principal;}
 public class LoanStatePayload:Payload{public List<LoanEntry> Loans=new();}
 public class LoanAnswerPayload{public string Id;public bool Accept;}
 public class LoanRepayPayload:Payload{public string Id,From;public float Amount;}
 public class LoanEntry{public string Id,Borrower,Lender;public float Remaining,DailyPayment,DailyInterest;}
 public class MPLink{}
 public static class GameStateReader {public static string AddressKey(string a)=>a;public static int Day=1;public static (int day,float hour) GetGameTime()=>(Day,0);}
 public static class MPConfig{public static string PlayerId="host";}
 public static class MPSaveCoordinator{public static string ActivePlaythroughId="world";public static int Persists;public static bool StoreManifest=true;public static Action Saving;public static void PersistGrantsNow(){Saving?.Invoke();if(StoreManifest)CheckedPersistence.ManifestStored();Persists++;}}
 public static class MergerSync{
  public static string MyGroupId="";public static Dictionary<string,string> Membership=new();
  public static string GroupOfStable(string s)=>s!=null&&Membership.TryGetValue(s,out var g)?g:"";
 }
 public static class MPServer{
  public static bool IsRunning=true;public static int WalletCalls;public static Dictionary<string,float> Cash=new();public static Action<string> Sending;
  public static List<(string,MessageType,object)> Sent=new();
  public static Dictionary<string,object> Fields=new(){["_groupInfo"]=new Dictionary<string,MergerGroupInfo>(),["_hostOffers"]=new Dictionary<string,LoanOfferPayload>(),["_hostLoans"]=new List<LoanEntry>(),["_lastDay"]=-1,["_pendingPlace"]=null,["_awaitFinalFrom"]=""};
  public static void SendHubTo<T>(string pid,MessageType t,T p){Sending?.Invoke(pid);Sent.Add((pid,t,p));}
  public static void RebroadcastMergerState(bool force){}
  public static float GetKnownCash(string p)=>Cash.TryGetValue(p,out var m)?m:-1;
  public static void RecordCash(string p,float m)=>Cash[p]=m;
  public static bool TryWalletBalance(string g,out float balance){balance=1000;return true;}
  public static void HostWalletDelta(string p,float a,string k,bool c)=>WalletCalls++;
 }
 public static class MPClient{public static bool IsConnected=true;public static List<MessageEnvelope> Sent=new();public static void SendEnvelope(MessageEnvelope e)=>Sent.Add(e);public static void SendInteriorRequest(string address){}}
 public static class MPHub{public static readonly List<LoanOfferPayload> IncomingOffers=new();public static void ReceiveOffer(LoanOfferPayload p){if(HubInputs.Offer(p)&&p.State=="offer")IncomingOffers.Add(p);}public static int Applies;public static void SaveLedger(){}public static void ApplyMoneyDelta(float amount,string reason,bool notice){SaveGameManager.Current.Money+=amount;Applies++;}}
 public static class Plugin{public static readonly TestLogger Logger=new();}
 public class TestLogger{public void LogInfo(object s){}public void LogWarning(object s){}}
 public static class MPLifecycle{}
 public static class MPRestSync {public static List<string> Online=new(){"host","friend"};public static IReadOnlyList<string> AllPlayers()=>Online;}
 public static class GameStatePatcher {public static Queue<Action> Pending=new();public static void ForgetInteriorBaseline(string address){}public static void EnqueueOnMainThread(Action a)=>Pending.Enqueue(a);public static void Drain(){while(Pending.TryDequeue(out var a))a();}}
}
namespace Helpers{}
namespace BigAmbitions.Items {public class CargoInstance{public string Name;public int amount;public bool paid;public List<CargoInstance> nestedCargoInstances=new();}}
public class VehicleInstance{public List<BigAmbitions.Items.CargoInstance> cargoInstances=new();}
