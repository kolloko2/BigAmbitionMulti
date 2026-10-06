using System;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using Helpers;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BAMP.CartTrunkFix
{
    public static class CartAccess
    {
        internal static Type ModType(string name) => typeof(ModEntry).Assembly.GetType("BigAmbitionsMP." + name, true);
        internal static MethodInfo Method(string type, string method) => AccessTools.Method(ModType(type), method);
        internal static object Read(string field) => AccessTools.Field(ModType("VehicleStoragePanel"), field).GetValue(null);
        internal static object Call(string type, string method, params object[] args) => Method(type, method).Invoke(null, args);
        public static VehicleInstance CurrentCart()
        {
            var cart = VehicleHelper.GetCurrentVehicle();
            return cart != null && cart.VehicleType != null && cart.VehicleType.spawnInPlayerObject
                && cart.VehicleType.maxCargoCapacity > 0 ? cart : null;
        }
        public static bool HasCartCargo()
        {
            var cart = CurrentCart();
            return cart?.cargoInstances != null && cart.cargoInstances.Count > 0;
        }
        internal static UnityAction DepositAction()
        {
            // Capture the target when building the button; later UI rebuilds must
            // never redirect a queued click to a different vehicle.
            var vid = (string)Read("_vid");
            var owner = (string)Read("_owner");
            return () => {
                if (!HasCartCargo() || string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(owner)) return;
                Call("PassengerRide", "WalkAndDeposit", vid, owner);
            };
        }
        internal static void Warn(Exception ex) => Plugin.Logger.LogWarning("[CartTrunkFix] " + ex);
    }

    [HarmonyPatch]
    public static class CartClick
    {
        private static MethodBase TargetMethod() => CartAccess.Method("PassengerRide", "HandleUnlockedClick");
        private static bool Prefix(string vid)
        {
            if ((!MPServer.IsRunning && !MPClient.IsConnected) || CartAccess.CurrentCart() == null) return true;
            try
            {
                // RouteGhostClick already performs the locked/access check. Keep
                // service vehicles on their original special-purpose route.
                if ((bool)CartAccess.Call("VehicleManager", "IsServiceGhost", vid)) return true;
                var owner = (string)CartAccess.Call("VehicleManager", "OwnerIdFor", vid);
                if (string.IsNullOrEmpty(owner)) return true;
                CartAccess.Call("VehicleStoragePanel", "Open", vid, owner);
                Plugin.Logger.LogWarning("[CartTrunkFix] cart trunk opened for '" + vid + "'.");
                return false;
            }
            catch (Exception ex) { CartAccess.Warn(ex); return true; }
        }
    }

    [HarmonyPatch]
    public static class CartNativeDeposit
    {
        private static MethodBase TargetMethod() => CartAccess.Method("VehicleStoragePanel", "PopulateCargoBody");
        private static void Postfix()
        {
            if (!CartAccess.HasCartCargo() || PlayerHelper.ItemInstanceInHands != null) return;
            try
            {
                var button = (Button)CartAccess.Read("_cargoSellAll");
                if (button == null) return;
                foreach (var component in button.GetComponentsInChildren<Component>(true))
                    if (component.GetType().Name == "TextLocalizationComponent" && component is Behaviour behaviour)
                        behaviour.enabled = false;
                var text = button.GetComponentInChildren<TMP_Text>(true);
                if (text != null) text.text = "Deposit cart";
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(CartAccess.DepositAction());
                button.gameObject.SetActive(true);
            }
            catch (Exception ex) { CartAccess.Warn(ex); }
        }
    }

    [HarmonyPatch]
    public static class CartFallbackDeposit
    {
        private static MethodBase TargetMethod() => CartAccess.Method("VehicleStoragePanel", "MakeDepositRow");
        private static void Postfix()
        {
            if (!CartAccess.HasCartCargo() || PlayerHelper.ItemInstanceInHands != null) return;
            try { CartAccess.Call("VehicleStoragePanel", "MakeWideButton", "Deposit cart", new Color(0.36f, 0.7f, 0.36f, 1f), CartAccess.DepositAction()); }
            catch (Exception ex) { CartAccess.Warn(ex); }
        }
    }
}
