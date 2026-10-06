// Managed test doubles only. Never packaged in the game/mod.
using System.Reflection;
namespace HarmonyLib {
 [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute {public HarmonyPatch(Type t,string m) {}}
 [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPriority : Attribute {public HarmonyPriority(int x){}}
 public static class Priority {public const int First=800;}
 public static class AccessTools {public static FieldInfo Field(Type t,string n) => t.GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);}
}
namespace UnityEngine {
 public class Object {
  public string name; public bool destroyed;
  public static bool operator ==(Object a,Object b) => ReferenceEquals(a,b) || ((ReferenceEquals(a,null)||a.destroyed) && (ReferenceEquals(b,null)||b.destroyed));
  public static bool operator !=(Object a,Object b) => !(a==b);
  public override bool Equals(object o)=>ReferenceEquals(this,o); public override int GetHashCode()=>base.GetHashCode();
 }
 public class Component : Object {
  public GameObject gameObject; public Transform transform=>gameObject.transform;
  public T GetComponent<T>() where T:Component => gameObject.Components.OfType<T>().FirstOrDefault();
  public T[] GetComponentsInChildren<T>(bool inactive) where T:Component => gameObject.Walk().SelectMany(g=>g.Components).OfType<T>().ToArray();
  public T GetComponentInChildren<T>(bool inactive) where T:Component => GetComponentsInChildren<T>(inactive).FirstOrDefault();
  public T GetComponentInParent<T>() where T:Component => GetComponent<T>() ?? transform.parent?.GetComponentInParent<T>();
 }
 public class MonoBehaviour:Component {}
 public class GameObject:Object {
  public List<Component> Components=new(); public Transform transform; public bool activeSelf;
  public GameObject(string n) {name=n; transform=Add<Transform>();}
  public T Add<T>() where T:Component,new() {var c=new T {gameObject=this,name=name};Components.Add(c);return c;}
  public void SetActive(bool b)=>activeSelf=b;
  public IEnumerable<GameObject> Walk()=>new[]{this}.Concat(transform.children.SelectMany(t=>t.gameObject.Walk()));
  public GameObject Child(string n) {var g=new GameObject(n);g.transform.parent=transform;transform.children.Add(g.transform);return g;}
 }
 public class Transform:Component {public Transform parent;public List<Transform> children=new();public Transform Find(string n)=>children.FirstOrDefault(c=>c.name==n);}
 public class RectTransform:Component {}
 public struct Color {public static Color white=>new();}
 public struct Color32 {public static implicit operator Color(Color32 c)=>new();}
 public static class Time {public static float unscaledTime;}
}
namespace TMPro {public class TMP_Text:UnityEngine.Component {public string text;public UnityEngine.Color color;}public class TextMeshProUGUI:TMP_Text {}}
namespace UnityEngine.UI {public class Button:UnityEngine.Component {}}
namespace UI {public class UIs:UnityEngine.Object {}}
namespace Extensions {public class NamespaceMarker {}}
namespace Streets {public static class Formatting {public static string ToFormattedString(this Address a)=>a.key;}}
namespace Localizor.LanguageChangeEvent {public class TextLocalizationComponent:UnityEngine.Component {public string value;public void SetValue(string s,bool b)=>value=s;}}
namespace BigAmbitionsMP {
 public static class MPServer {public static bool IsRunning;}
 public static class Plugin {public static Log Logger=new();}
 public class Log {public List<string> Lines=new();public void LogWarning(object x)=>Lines.Add(x.ToString());public void LogError(object x)=>Lines.Add(x.ToString());}
}
public class Address {public string key;public Address(string k){key=k;}public static bool operator ==(Address a,Address b)=>ReferenceEquals(a,b)||(!ReferenceEquals(a,null)&&!ReferenceEquals(b,null)&&a.key==b.key);public static bool operator !=(Address a,Address b)=>!(a==b);public override bool Equals(object x)=>x is Address a&&this==a;public override int GetHashCode()=>key.GetHashCode();}
public class Building:UnityEngine.Object {public Address Address;public string BuildingType;}
public class BuildingRegistration {public Address Address;public bool RentedByPlayer;public string businessTypeName;}
public class RealEstate {public Address address;}
public class GameInstance {public List<RealEstate> realEstate=new();public decimal Money=1000;}
public static class SaveGameManager {public static GameInstance Current=new();}
namespace Helpers {public static class BuildingHelper {
 public static Dictionary<string,Building> Buildings=new();public static Dictionary<string,BuildingRegistration> Registrations=new();
 public static Building GetBuilding(Address a)=>Buildings.TryGetValue(a.key,out var b)?b:null;
 public static BuildingRegistration GetBuildingRegistration(Address a)=>Registrations.TryGetValue(a.key,out var r)?r:null;
}}
public static class InstanceBehavior<T> {public static T Instance;}
public class GlobalReferences:UnityEngine.Object {public Colors colors=new();}
public class Colors {public UnityEngine.Color32 white;}
public class SplitterIndicator:UnityEngine.Component {private UnityEngine.RectTransform indicatorRect;public int Calls;public void Set(UnityEngine.RectTransform x)=>Calls++;}
public class BizManBuildingInfo:UnityEngine.Component {public int Calls;public void ShowInfo(){Calls++;gameObject.SetActive(true);}}
public class BizManPresentation:UnityEngine.Component {private BizManBusiness bizManBusiness;}
public class CountingTab:UnityEngine.Component {public int Calls;public void RefreshData(){Calls++;}public void RefreshData(BuildingRegistration r){Calls++;}public void LoadScheduler(){Calls++;}}
public class BizManBusiness:UnityEngine.MonoBehaviour {
 public Address address;public Building building;public BuildingRegistration buildingRegistration;
 public CountingTab bizManInsight,bizManInventoryPricing,bizManSchedule,bizManSettings,bizManMarketing;
 private UnityEngine.Transform menu,containers,businessInfo,_currentContainer;
 private BizManBuildingInfo buildingInfo;private SplitterIndicator splitterIndicator;
 private TMPro.TextMeshProUGUI _currentTabText;private UnityEngine.Color32 _defaultTabColor;
 private string _selectedTab;private List<string> _tabs;
 public void ScheduleLoadAlerts(){}
}
namespace UI.Smartphone.Apps.BizMan {public class NamespaceMarker {}}
namespace UI.Elements {
 public class DropdownOptionModel {public string option;public bool selected;public int optionId;}
 public class DropdownOptionCellView:UnityEngine.Component {public TMPro.TMP_Text optionText;public UnityEngine.GameObject selectedGo;}
}
