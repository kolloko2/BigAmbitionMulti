using BAMP.HostBizManFix;
using BigAmbitionsMP;
using UnityEngine;
using TMPro;
using System.Reflection;

void Check(bool b,string name) {if(!b)throw new Exception(name);Console.WriteLine("PASS: "+name);}
object Prefix(Type t,params object[] args)=>t.GetMethod("Prefix",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args);
BizManBusiness Page(string key="42") {
 var page=new GameObject("Business").Add<BizManBusiness>();page.address=new Address(key);
 Helpers.BuildingHelper.Buildings[key]=new Building {Address=new Address(key),BuildingType="ba:buildingtype_retail"};
 Helpers.BuildingHelper.Registrations[key]=new BuildingRegistration {Address=new Address(key),businessTypeName="ba:businesstype_empty"};
 var menu=page.gameObject.Child("Menu");var button=menu.Child("Presentation");button.Add<UnityEngine.UI.Button>();button.Child("Text").Add<TextMeshProUGUI>();
 var containers=page.gameObject.Child("Containers");containers.Child("Presentation").Add<BizManPresentation>();
 page.gameObject.Child("BuildingInfo").Add<BizManBuildingInfo>();
 page.gameObject.Child("BusinessInfo");
 Recovery.Write(page,"_tabs",new List<string>{"Presentation"});
 page.bizManSettings=new CountingTab();page.bizManInsight=new CountingTab();page.bizManInventoryPricing=new CountingTab();page.bizManSchedule=new CountingTab();page.bizManMarketing=new CountingTab();
 return page;
}

MPServer.IsRunning=false;
Check((bool)Prefix(typeof(HostFix_SetInitialTab),null,null),"nonhost retains original SetInitialTab");
string t="Presentation";
Check((bool)Prefix(typeof(HostFix_SetTab),null,t),"nonhost retains original SetTab");
MPServer.IsRunning=true;
var p=Page();
Check(Recovery.EnsureData(p)&&p.buildingRegistration!=null&&p.building!=null,"same-address missing data is re-resolved");
Check(SaveGameManager.Current.Money==1000,"reference recovery does not change money");
Recovery.BindPage(p);
Check(Recovery.Read<Transform>(p,"menu")!=null&&Recovery.Read<Transform>(p,"containers")!=null,"unambiguous local menu and containers rebound");
Check(Recovery.SetTab(p,"Presentation"),"presentation opens with missing previous text");
Check(Recovery.Read<TextMeshProUGUI>(p,"_currentTabText")!=null,"nested TMP label is found");
var target=Recovery.Read<Transform>(p,"_currentContainer");
Check(target.gameObject.activeSelf,"presentation container activated");
Recovery.Write(p,"_currentTabText",null);
Check(Recovery.SetTab(p,"Presentation"),"reopening dangling current tab succeeds");
Check(Recovery.SetTab(p,"Settings")&&Recovery.Read<string>(p,"_selectedTab")=="Presentation"&&p.bizManSettings.Calls==0,"forbidden management tab falls back without invoking settings");
Recovery.Write(p,"_tabs",new List<string>{"Presentation","Missing"});
Check(!Recovery.SetTab(p,"Missing")&&target.gameObject.activeSelf&&Recovery.Read<string>(p,"_selectedTab")=="Presentation","missing target does not deactivate previous content");
Recovery.Write(p,"_currentTabText",null);
Prefix(typeof(HostFix_OnDisable),p);
Check(Recovery.Read<Transform>(p,"_currentContainer")==null&&!target.gameObject.activeSelf,"dangling-tab cleanup allows native OnDisable to finish");
p.buildingRegistration.RentedByPlayer=true;p.buildingRegistration.businessTypeName="ba:businesstype_warehouse";
Prefix(typeof(HostFix_SetInitialTab),p,"Schedule");
Check(Recovery.Read<string>(p,"_selectedTab")=="Drivers","warehouse Schedule request retains native Drivers mapping");
p.buildingRegistration.RentedByPlayer=false;
Prefix(typeof(HostFix_SetInitialTab),p,null);
Check(Recovery.Read<string>(p,"_selectedTab")=="Presentation","unrented building defaults to Presentation");
var unknown=Page("missing");Helpers.BuildingHelper.Buildings.Remove("missing");
Check(!Recovery.EnsureData(unknown)&&unknown.buildingRegistration==null,"unresolvable building does not fabricate registration");
var dropdown=new GameObject("Option").Add<UI.Elements.DropdownOptionCellView>();
dropdown.gameObject.Child("Text").Add<TextMeshProUGUI>();
var model=new UI.Elements.DropdownOptionModel {option="Address 42",optionId=3};
Check(!(bool)Prefix(typeof(HostFix_Dropdown),dropdown,model)&&dropdown.optionText.text==model.option&&dropdown.gameObject.name=="DropdownOption3","dropdown recovers text without selection-highlight object");
Check(SaveGameManager.Current.Money==1000,"all UI regressions preserve money");
Console.WriteLine("All 17 managed regression checks passed. Unity runtime visual verification is separate.");
