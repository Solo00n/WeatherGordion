using System;
using HarmonyLib;

namespace WeatherGordion.Patches;

/// <summary>
/// Puts Gordion's weather pool and weights in place immediately before weather is picked for the day.
///
/// WeatherRegistry replaces <c>SetPlanetsWeather</c> wholesale, and its selection reads the moon's
/// <c>randomWeathers</c> array. Running at <see cref="Priority.First"/> guarantees this mod has already
/// filled that array by the time the replacement runs, whichever order the two plugins loaded in — and
/// it re-applies every day, so nothing is lost if something else rebuilds the pool in between.
/// </summary>
[HarmonyPatch]
internal static class SetPlanetsWeatherPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(StartOfRound), "SetPlanetsWeather")]
    private static void SetPlanetsWeather_Prefix(StartOfRound __instance)
    {
        try
        {
            // Covers the very first call of a session, before StartOfRound.Awake's postfix has run in
            // load orders where the weather pass happens first.
            GordionLevel.Resolve(__instance);
            WeatherUnlocker.Apply("weather is being selected");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not prepare Gordion before weather selection: {e}");
        }
    }
}
