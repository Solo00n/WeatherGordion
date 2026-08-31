using System;
using HarmonyLib;

namespace WeatherGordion.Patches;

[HarmonyPatch]
internal static class StartOfRoundPatches
{
    /// <summary>
    /// Resolves the Company moon as early as the moon list exists — and, importantly, before anything
    /// this mod does could disturb the flags other code identifies it by.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), "Awake")]
    private static void Awake_Postfix(StartOfRound __instance)
    {
        try
        {
            GordionLevel.Reset();
            WeatherUnlocker.Reset();
            GordionLevel.Resolve(__instance);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"StartOfRound.Awake handler failed: {e}");
        }
    }

    /// <summary>Ship is on its way up: stop our clock before the round starts tearing down.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
    private static void ShipLeave_Prefix()
    {
        try
        {
            TimeController.OnLeftGordion();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ShipLeave handler failed: {e}");
        }
    }

    /// <summary>
    /// Last chance to hand the deadline back before the game decides how much time the round cost.
    /// PassTimeToNextDay reads timeUntilDeadline, and on Gordion it must see the value from before
    /// the visit.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(StartOfRound), "PassTimeToNextDay")]
    private static void PassTimeToNextDay_Prefix()
    {
        try
        {
            TimeController.RestoreImmediately();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"PassTimeToNextDay handler failed: {e}");
        }
    }

    /// <summary>Disconnect or return to menu: drop everything we cached for this session.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), "OnDestroy")]
    private static void OnDestroy_Postfix()
    {
        try
        {
            TimeController.RestoreImmediately();
            SimulatedClock.Stop();
            GordionLevel.Reset();
            WeatherUnlocker.Reset();
        }
        catch
        {
            // The scene is being destroyed; nothing useful to log.
        }
    }
}
