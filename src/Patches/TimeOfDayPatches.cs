using System;
using HarmonyLib;

namespace WeatherGordion.Patches;

/// <summary>
/// Holds the quota deadline still while this mod runs the clock on Gordion.
///
/// <c>TimeOfDay.MoveGlobalTime</c> subtracts every elapsed frame from <c>timeUntilDeadline</c> with no
/// regard for which moon you are on — vanilla simply never calls it at the Company, because the clock
/// there is stopped. Starting that clock therefore has two knock-on effects that do not exist in the
/// base game: selling trips eat into the deadline, and the company buying rate moves with it (it is
/// computed from <c>daysUntilDeadline</c>). Putting the value back each frame removes both.
/// </summary>
[HarmonyPatch]
internal static class TimeOfDayPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.MoveGlobalTime))]
    private static void MoveGlobalTime_Postfix(TimeOfDay __instance)
    {
        try
        {
            if (TimeController.TryGetFrozenDeadline(out float held))
                __instance.timeUntilDeadline = held;
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Deadline hold failed: {e.Message}");
        }
    }
}
