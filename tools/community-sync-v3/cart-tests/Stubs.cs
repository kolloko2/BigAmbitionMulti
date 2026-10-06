using System.Reflection;
namespace HarmonyLib {
 [AttributeUsage(AttributeTargets.Class)] public class HarmonyPatch:Attribute {}
 public static class AccessTools {
  public static MethodInfo Method(Type t,string n)=>t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
  public static FieldInfo Field(Type t,string n)=>t.GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
 }
}
namespace UnityEngine {
 public class Component {
  public GameObject gameObject=new(); public List<Component> children=new();
  public T[] GetComponentsInChildren<T>(bool b) where T:Component=>children.OfType<T>().ToArray();
  public T GetComponentInChildren<T>(bool b) where T:Component=>children.OfType<T>().FirstOrDefault();
 }
 public class GameObject {public bool active;public void SetActive(bool b)=>active=b;}
 public class Behaviour:Component {public bool enabled=true;}
 public struct Color {public Color(float r,float g,float b,float a){}}
}
namespace UnityEngine.Events {public delegate void UnityAction();}
namespace UnityEngine.UI {public class Button:UnityEngine.Component {
 public ButtonClickedEvent onClick=new();
 public class ButtonClickedEvent {public List<UnityEngine.Events.UnityAction> listeners=new();public void AddListener(UnityEngine.Events.UnityAction a)=>listeners.Add(a);public void Invoke(){foreach(var a in listeners.ToArray())a();}}
}}
namespace TMPro {public class TMP_Text:UnityEngine.Component {public string text;}}
public class VehicleType {public bool spawnInPlayerObject;public int maxCargoCapacity;}
public class VehicleInstance {public VehicleType VehicleType;public List<object> cargoInstances=new();}
namespace Helpers {
 public static class VehicleHelper {public static VehicleInstance Current;public static VehicleInstance GetCurrentVehicle()=>Current;}
 public static class PlayerHelper {public static object ItemInstanceInHands;}
}
namespace BigAmbitionsMP {
 public class ModEntry {}
 public static class MPServer {public static bool IsRunning=true;}
 public static class MPClient {public static bool IsConnected;}
 public class Log {public void LogWarning(object o){}}
 public static class Plugin {public static Log Logger=new();}
 internal static class VehicleManager {public static bool Service;public static string Owner="friend";public static bool IsServiceGhost(string v)=>Service;public static string OwnerIdFor(string v)=>Owner;}
 internal static class PassengerRide {public static string DepositVid;public static int Deposits;private static void HandleUnlockedClick(string vid){}private static void WalkAndDeposit(string v,string o){DepositVid=v;Deposits++;}}
 internal static class VehicleStoragePanel {
  public static string _vid,_owner;public static UnityEngine.UI.Button _cargoSellAll;public static int Opens,Rows;public static UnityEngine.Events.UnityAction Fallback;
  public static void Open(string v,string o){_vid=v;_owner=o;Opens++;}
  private static void PopulateCargoBody(){}private static void MakeDepositRow(){}
  private static void MakeWideButton(string label,UnityEngine.Color c,UnityEngine.Events.UnityAction action){Rows++;Fallback=action;}
 }
}
