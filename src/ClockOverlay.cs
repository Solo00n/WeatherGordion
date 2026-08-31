using UnityEngine;

namespace WeatherGordion;

/// <summary>
/// Draws the HUD clock on Gordion.
///
/// Vanilla only ever touches clock visibility from <c>TimeOfDay.SetInsideLightingDimness</c>, which is
/// reached from <c>MoveTimeOfDay</c> and only when <c>sunAnimator</c> is not null — and it hides the
/// clock whenever the player counts as indoors, which on a selling trip is nearly the whole visit.
///
/// <c>SetClockVisible</c> alone is not enough to rely on: it only nudges <c>HUDElement.targetAlpha</c>,
/// which <c>HUDManager.Update</c> then lerps the canvas group towards, so anything writing that field
/// later in the frame wins and a disabled element never shows at all. The alpha is therefore written
/// directly as well, from LateUpdate, after TimeOfDay has had its say for the frame.
/// </summary>
internal static class ClockOverlay
{
    private static bool _loggedState;

    public static void Draw(float normalizedTimeOfDay)
    {
        if (!Plugin.Cfg.ShowClock.Value)
            return;

        var hud = HUDManager.Instance;
        var time = TimeOfDay.Instance;
        if (hud == null || time == null)
            return;

        hud.SetClockVisible(true);
        hud.SetClock(normalizedTimeOfDay, time.numberOfHours);
        hud.SetClockIcon(time.dayMode);

        CanvasGroup group = hud.Clock?.canvasGroup;
        if (group == null)
        {
            LogStateOnce("HUDManager.Clock has no canvas group — the clock cannot be shown.");
            return;
        }

        if (!group.gameObject.activeSelf)
            group.gameObject.SetActive(true);

        // Straight to 1 rather than waiting on the lerp, so no other writer can hold it at 0.
        group.alpha = 1f;

        LogStateOnce(
            $"Clock asserted: active {group.gameObject.activeInHierarchy}, alpha {group.alpha:0.##}, " +
            $"numberOfHours {time.numberOfHours}, text '{hud.clockNumber?.text}'.");
    }

    public static void Hide()
    {
        _loggedState = false;

        if (!Plugin.Cfg.ShowClock.Value)
            return;

        var hud = HUDManager.Instance;
        if (hud == null)
            return;

        try
        {
            hud.SetClockVisible(false);
        }
        catch
        {
            // The HUD is going away with the round.
        }
    }

    private static void LogStateOnce(string message)
    {
        if (_loggedState)
            return;

        _loggedState = true;
        Plugin.DebugLog(message);
    }
}
