using System;
using HarmonyLib;

namespace WeatherGordion.Patches;

/// <summary>
/// Guarantees Gordion's weather pool exists on every machine at the one moment it is read.
///
/// WeatherRegistry syncs the *choice* of weather — a NetworkVariable carrying moon name and weather
/// type — but not the numbers behind it. Each client then runs
/// <c>RoundManager.SetToCurrentLevelWeather</c> from its own level generation and copies
/// <c>weatherVariable</c>/<c>weatherVariable2</c> out of its own <c>randomWeathers</c> entry into
/// <c>TimeOfDay</c>. A client whose Gordion pool is still empty finds no entry, keeps whatever those
/// fields held before, and ends up with the flood at a different height and the fog at a different
/// density than the host — same weather, different world.
///
/// The pool is filled from the <c>SetPlanetsWeather</c> patch as well, but on a client that only runs
/// when they join. Rebuilding it here, immediately before the read, removes the ordering question
/// entirely: it happens on every player, on every landing.
/// </summary>
[HarmonyPatch]
internal static class RoundManagerPatches
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(RoundManager), "SetToCurrentLevelWeather")]
    private static void SetToCurrentLevelWeather_Prefix()
    {
        try
        {
            WeatherUnlocker.Apply("the level is applying its weather");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not prepare Gordion's weather variables: {e}");
        }
    }
}
