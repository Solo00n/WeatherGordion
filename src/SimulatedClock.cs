using System;
using UnityEngine;

namespace WeatherGordion;

/// <summary>
/// The <see cref="GordionTimeMode.Simulated"/> fallback: a day that passes on Gordion without the
/// game's clock ever being started.
///
/// Nothing here touches <c>currentDayTimeStarted</c>, <c>globalTime</c> or the deadline, so TimeOfDay
/// stays exactly as inert as it is in vanilla and no other mod can be surprised by a Company visit
/// suddenly having a clock. In exchange, everything the vanilla clock would have done has to be done
/// by hand: the normalised time is written directly, the HUD clock is drawn from it, and weather
/// stages are stepped by <see cref="ProgressionDriver"/> instead of by WeatherTweaks' hook into
/// <c>MoveTimeOfDay</c> (which never runs here).
/// </summary>
internal sealed class SimulatedClock : MonoBehaviour
{
    private static SimulatedClock _instance;

    private float _elapsed;
    private float _dayLength;

    /// <summary>Progress through the simulated day, 0 at landing to 1 at its end.</summary>
    public static float Normalized => _instance != null ? _instance.Current : 0f;

    /// <summary>True while a simulated day is running.</summary>
    public static bool Running => _instance != null;

    private float Current => _dayLength <= 0f ? 0f : Mathf.Clamp01(_elapsed / _dayLength);

    public static void Begin()
    {
        if (_instance != null)
            return;

        var go = new GameObject("WeatherGordion_SimulatedClock");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;

        _instance = go.AddComponent<SimulatedClock>();
        _instance._dayLength = Mathf.Max(1f, Plugin.Cfg.DayLengthSeconds.Value);

        ProgressionDriver.Begin();

        Plugin.Log.LogInfo(
            $"Simulated Gordion day started ({_instance._dayLength:0} s). The game clock is untouched.");
    }

    public static void Stop()
    {
        if (_instance == null)
            return;

        ProgressionDriver.Stop();
        ClockOverlay.Hide();

        Destroy(_instance.gameObject);
        _instance = null;

        Plugin.DebugLog("Simulated Gordion day stopped.");
    }

    private void Update()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Simulated clock tick failed: {e.Message}");
        }
    }

    private void Tick()
    {
        if (_elapsed < _dayLength)
            _elapsed += Time.deltaTime;

        float normalized = Current;

        var time = TimeOfDay.Instance;
        if (time != null)
        {
            // Written straight in: with currentDayTimeStarted false, TimeOfDay.Update never touches
            // these, so anything reading them (weather effects, other mods) sees our day instead of a
            // permanent midnight.
            time.normalizedTimeOfDay = normalized;
            time.currentDayTime = normalized * time.totalTime;

            ClockOverlay.Draw(normalized);
        }

        ProgressionDriver.Tick(normalized);
    }
}
