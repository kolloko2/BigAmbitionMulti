using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using Extensions;
using HarmonyLib;
using Helpers;
using Localizor.LanguageChangeEvent;
using Streets;
using TMPro;
using UI;
using UI.Elements;
using UI.Smartphone.Apps.BizMan;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BAMP.HostBizManFix
{
    // Merged into the existing mod. Its official loader discovers these Harmony
    // classes and unpatches them with the original mod on unload.
    public static class Recovery
    {
        public const string Build = "host-bizman-1";
        private static readonly Dictionary<string, float> NextLog = new Dictionary<string, float>();
        private static readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();

        public static bool Active => MPServer.IsRunning;

        public static FieldInfo Field(string name)
        {
            if (!Fields.TryGetValue(name, out var f))
                Fields[name] = f = AccessTools.Field(typeof(BizManBusiness), name);
            if (f == null) throw new MissingFieldException(typeof(BizManBusiness).FullName, name);
            return f;
        }

        public static T Read<T>(BizManBusiness page, string name) => (T)Field(name).GetValue(page);
        public static void Write(BizManBusiness page, string name, object value) => Field(name).SetValue(page, value);

        public static void Log(string key, string message, bool error = false)
        {
            float now = Time.unscaledTime;
            if (NextLog.TryGetValue(key, out var at) && now >= at - 30f && now < at) return;
            NextLog[key] = now + 30f;
            if (error) Plugin.Logger.LogError("[HostBizManFix] " + message);
            else Plugin.Logger.LogWarning("[HostBizManFix] " + message);
        }

        public static bool Alive(Object value) => value != null;

        // Only use an unambiguous component belonging to THIS page. Never borrow
        // bindings from a different FullMenu or synthesize missing scene assets.
        private static T Unique<T>(BizManBusiness page) where T : Component
        {
            var found = page.GetComponentsInChildren<T>(true).Where(x => Alive(x)).ToArray();
            return found.Length == 1 ? found[0] : null;
        }

        private static Transform FindRoot(BizManBusiness page, bool isMenu)
        {
            var found = page.GetComponentsInChildren<Transform>(true).Where(t =>
            {
                var presentation = t.Find("Presentation");
                if (!Alive(presentation)) return false;
                return isMenu ? Alive(presentation.GetComponent<Button>()) :
                    Alive(presentation.GetComponentInChildren<BizManPresentation>(true));
            }).ToArray();
            return found.Length == 1 ? found[0] : null;
        }

        private static void Bind<T>(BizManBusiness page, string name, Func<T> find) where T : Object
        {
            if (Alive(Read<T>(page, name))) return;
            T candidate = find();
            if (!Alive(candidate)) return;
            Write(page, name, candidate);
            Log("binding:" + name, "restored '" + name + "' on " + page.name);
        }

        public static void BindPage(BizManBusiness page)
        {
            Bind(page, "menu", () => FindRoot(page, true));
            Bind(page, "containers", () => FindRoot(page, false));
            Bind(page, "buildingInfo", () => Unique<BizManBuildingInfo>(page));
            Bind(page, "splitterIndicator", () => Unique<SplitterIndicator>(page));
            Bind(page, "businessInfo", () =>
            {
                var found = page.GetComponentsInChildren<Transform>(true)
                    .Where(t => string.Equals(t.name, "BusinessInfo", StringComparison.OrdinalIgnoreCase)).ToArray();
                return found.Length == 1 ? found[0] : null;
            });
        }

        public static bool EnsureData(BizManBusiness page)
        {
            if (!Alive(page) || SaveGameManager.Current == null || page.address == null) return false;
            // Re-resolve null or mismatching data even when SetAddress would skip
            // its work because the same address is being opened a second time.
            if (!Alive(page.building) || page.building.Address != page.address)
            {
                page.building = BuildingHelper.GetBuilding(page.address);
                Log("building", "re-resolved building for " + page.address.ToFormattedString());
            }
            if (!Alive(page.building)) return false;
            if (page.buildingRegistration == null || page.buildingRegistration.Address != page.address)
            {
                page.buildingRegistration = BuildingHelper.GetBuildingRegistration(page.address);
                Log("registration", "re-resolved registration for " + page.address.ToFormattedString());
            }
            return page.buildingRegistration != null;
        }

        public static TMP_Text TextOn(Transform tab)
        {
            if (!Alive(tab)) return null;
            var direct = tab.GetComponent<TMP_Text>();
            if (Alive(direct)) return direct;
            var all = tab.GetComponentsInChildren<TMP_Text>(true).Where(x => Alive(x)).ToArray();
            return all.Length == 1 ? all[0] : null;
        }

        public static string State(BizManBusiness p)
        {
            if (!Alive(p)) return "page=null";
            return "page=" + p.name + " address=" + (p.address == null ? "null" : p.address.ToFormattedString()) +
                " building=" + Alive(p.building) + " registration=" + (p.buildingRegistration != null) +
                " menu=" + Alive(Read<Transform>(p, "menu")) +
                " containers=" + Alive(Read<Transform>(p, "containers")) +
                " currentText=" + Alive(Read<TextMeshProUGUI>(p, "_currentTabText"));
        }

        public static bool SetTab(BizManBusiness page, string requested)
        {
            BindPage(page);
            if (!EnsureData(page))
            {
                Log("tab-data", "cannot open tab before building data is ready: " + State(page), true);
                return false;
            }
            var menu = Read<Transform>(page, "menu");
            var containers = Read<Transform>(page, "containers");
            var allowed = Read<List<string>>(page, "_tabs");
            string tabName = requested;
            if (allowed != null && !allowed.Contains(tabName))
                tabName = allowed.Contains("Presentation") ? "Presentation" : null;
            if (string.IsNullOrEmpty(tabName) || !Alive(menu) || !Alive(containers))
            {
                Log("tab-bindings", "missing required tab bindings: " + State(page), true);
                return false;
            }
            var target = containers.Find(tabName);
            var text = TextOn(menu.Find(tabName));
            // Validate BEFORE disabling the old tab or activating the new one.
            // A nested TMP component is supported; a missing label is cosmetic.
            if (!Alive(target))
            {
                Log("tab-target:" + tabName, "tab container missing: " + tabName + "; " + State(page), true);
                return false;
            }
            var old = Read<Transform>(page, "_currentContainer");
            var oldText = Read<TextMeshProUGUI>(page, "_currentTabText");
            if (Alive(old)) old.gameObject.SetActive(false);
            if (Alive(oldText)) oldText.color = Read<Color32>(page, "_defaultTabColor");
            Write(page, "_selectedTab", tabName);
            Write(page, "_currentContainer", target);
            Write(page, "_currentTabText", text as TextMeshProUGUI);
            // Activation runs the game's BizManPresentation.OnEnable, including
            // its normal RentView and ownership checks. No forced rent visibility.
            if (!target.gameObject.activeSelf) target.gameObject.SetActive(true);
            if (Alive(text))
            {
                var globals = InstanceBehavior<GlobalReferences>.Instance;
                text.color = Alive(globals) ? (Color)globals.colors.white : Color.white;
                var splitter = Read<SplitterIndicator>(page, "splitterIndicator");
                var rect = text.GetComponent<RectTransform>();
                var splitterRect = Alive(splitter) ? AccessTools.Field(typeof(SplitterIndicator), "indicatorRect")?.GetValue(splitter) as RectTransform : null;
                if (Alive(splitter) && Alive(rect) && Alive(splitterRect)) splitter.Set(rect);
            }
            else Log("tab-text:" + tabName, "no TMP label for " + tabName + "; content remains usable");
            var businessInfo = Read<Transform>(page, "businessInfo");
            var buildingInfo = Read<BizManBuildingInfo>(page, "buildingInfo");
            if (Alive(businessInfo)) businessInfo.gameObject.SetActive(tabName != "Presentation" && tabName != "RealEstate");
            if (tabName == "RealEstate")
            {
                if (Alive(buildingInfo)) buildingInfo.ShowInfo();
            }
            else if (Alive(buildingInfo)) buildingInfo.gameObject.SetActive(false);
            switch (tabName)
            {
                case "Insight": page.bizManInsight.RefreshData(page.buildingRegistration); break;
                case "InventoryPricing": page.bizManInventoryPricing.RefreshData(); break;
                case "Schedule": page.bizManSchedule.LoadScheduler(); page.ScheduleLoadAlerts(); break;
                case "Settings": page.bizManSettings.RefreshData(); break;
                case "Marketing": page.bizManMarketing.RefreshData(); break;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BizManBusiness), "SetAddress")]
    public static class HostFix_SetAddress
    {
        private static bool Prefix(BizManBusiness __instance, Address newAddress)
        {
            if (!Recovery.Active) return true;
            Recovery.BindPage(__instance);
            __instance.address = newAddress;
            if (!Recovery.EnsureData(__instance))
            {
                Recovery.Log("address-data", "cannot resolve requested building: " + Recovery.State(__instance), true);
                return false;
            }
            var menu = Recovery.Read<Transform>(__instance, "menu");
            var presentation = Recovery.Alive(menu) ? menu.Find("Presentation") : null;
            if (Recovery.Alive(presentation))
            {
                var label = presentation.GetComponent<TextLocalizationComponent>();
                if (!Recovery.Alive(label))
                {
                    var all = presentation.GetComponentsInChildren<TextLocalizationComponent>(true);
                    if (all.Length == 1) label = all[0];
                }
                if (Recovery.Alive(label)) label.SetValue(newAddress.ToFormattedString(), true);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(BizManBusiness), "SetInitialTab")]
    public static class HostFix_SetInitialTab
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(BizManBusiness __instance, string tab)
        {
            if (!Recovery.Active) return true;
            Recovery.BindPage(__instance);
            if (!Recovery.EnsureData(__instance))
            {
                Recovery.Write(__instance, "_selectedTab", "Presentation");
                Recovery.Log("initial-data", "building data not ready: " + Recovery.State(__instance), true);
                return false;
            }
            var reg = __instance.buildingRegistration;
            string selected = tab;
            if (selected == null)
            {
                if (!reg.RentedByPlayer || reg.businessTypeName == "ba:businesstype_empty") selected = "Presentation";
                else if (reg.businessTypeName == "ba:businesstype_headquarters") selected = "PricingManagers";
                else if (reg.businessTypeName == "ba:businesstype_factory") selected = "Factory";
                else if (__instance.building.BuildingType == "ba:buildingtype_warehouse") selected = "Drivers";
                else if (__instance.building.BuildingType == "ba:buildingtype_residential")
                    selected = SaveGameManager.Current.realEstate != null && SaveGameManager.Current.realEstate.Exists(x => x.address == __instance.address) ? "RealEstate" : "Presentation";
                else selected = "Insight";
            }
            if (selected == "Schedule" && reg.businessTypeName == "ba:businesstype_warehouse") selected = "Drivers";
            Recovery.Write(__instance, "_selectedTab", selected);
            return false;
        }
    }

    [HarmonyPatch(typeof(BizManBusiness), "SetTab")]
    public static class HostFix_SetTab
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(BizManBusiness __instance, ref string tabName)
        {
            if (!Recovery.Active) return true;
            if (Recovery.SetTab(__instance, tabName))
                tabName = Recovery.Read<string>(__instance, "_selectedTab");
            return false;
        }
        private static Exception Finalizer(BizManBusiness __instance, Exception __exception)
        {
            if (Recovery.Active && __exception != null)
                Recovery.Log("tab-exception", Recovery.State(__instance) + "\n" + __exception, true);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BizManBusiness), "OnDisable")]
    public static class HostFix_OnDisable
    {
        private static void Prefix(BizManBusiness __instance)
        {
            if (!Recovery.Active || Recovery.Alive(Recovery.Read<TextMeshProUGUI>(__instance, "_currentTabText"))) return;
            var current = Recovery.Read<Transform>(__instance, "_currentContainer");
            // The game dereferences currentText when currentContainer is nonnull.
            // Finish the visual cleanup, then let its normal events/reset run.
            Recovery.Write(__instance, "_currentContainer", null);
            if (Recovery.Alive(current)) current.gameObject.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(BizManBusiness), "RefreshData")]
    public static class HostFix_RefreshData
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(BizManBusiness __instance)
        {
            if (!Recovery.Active) return true;
            Recovery.BindPage(__instance);
            if (Recovery.EnsureData(__instance)) return true;
            Recovery.Log("refresh-data", "refresh skipped until building data is ready; reopen building info: " + Recovery.State(__instance), true);
            return false;
        }
        private static Exception Finalizer(BizManBusiness __instance, Exception __exception)
        {
            if (Recovery.Active && __exception != null)
                Recovery.Log("refresh-exception", Recovery.State(__instance) + "\n" + __exception, true);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BizManPresentation), "OnEnable")]
    public static class HostFix_PresentationOwner
    {
        private static void Prefix(BizManPresentation __instance)
        {
            if (!Recovery.Active) return;
            var field = AccessTools.Field(typeof(BizManPresentation), "bizManBusiness");
            var owner = field.GetValue(__instance) as BizManBusiness;
            if (Recovery.Alive(owner)) return;
            var parent = __instance.GetComponentInParent<BizManBusiness>();
            if (!Recovery.Alive(parent)) return;
            field.SetValue(__instance, parent);
            Recovery.Log("presentation-owner", "restored presentation page reference");
        }
    }

    [HarmonyPatch(typeof(DropdownOptionCellView), "SetData")]
    public static class HostFix_Dropdown
    {
        private static bool Prefix(DropdownOptionCellView __instance, DropdownOptionModel data)
        {
            if (!Recovery.Active) return true;
            if (data == null)
            {
                Recovery.Log("dropdown-data", "empty dropdown model on " + __instance.name, true);
                return false;
            }
            if (!Recovery.Alive(__instance.optionText))
            {
                var texts = __instance.GetComponentsInChildren<TMP_Text>(true).Where(x => Recovery.Alive(x)).ToArray();
                if (texts.Length == 1) __instance.optionText = texts[0];
            }
            if (!Recovery.Alive(__instance.optionText))
            {
                Recovery.Log("dropdown-text", "dropdown TMP label unavailable on " + __instance.name, true);
                return false;
            }
            __instance.optionText.text = data.option ?? "";
            if (Recovery.Alive(__instance.selectedGo)) __instance.selectedGo.SetActive(data.selected);
            __instance.gameObject.name = "DropdownOption" + data.optionId;
            return false;
        }
    }
}
