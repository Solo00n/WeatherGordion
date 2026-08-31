using System;
using System.Collections.Generic;
using WeatherGordion.Compat;
using WeatherRegistry;

namespace WeatherGordion;

/// <summary>
/// Steps a progressing weather through its stages during a <see cref="SimulatedClock"/> day.
///
/// In RealTime mode this class does nothing: WeatherTweaks already hooks <c>TimeOfDay.MoveTimeOfDay</c>
/// and switches stages itself once the clock is running. In Simulated mode that hook never fires, so
/// the stage boundaries are walked here instead and each one is applied through WeatherRegistry — the
/// same route the <c>weather change</c> terminal command takes, which means clients are brought along
/// by WeatherRegistry's own sync and do not need this mod installed.
///
/// Only the host decides, for the same reason the vanilla mid-day change is host-only: two machines
/// stepping the same weather would fight over it.
/// </summary>
internal static class ProgressionDriver
{
    private static List<ProgressionStage> _stages;
    private static int _nextStage;
    private static bool _active;

    public static void Begin()
    {
        _stages = null;
        _nextStage = 0;
        _active = false;

        if (!IsHost())
            return;

        SelectableLevel gordion = GordionLevel.Level;
        if (gordion == null)
            return;

        try
        {
            Weather current = WeatherManager.GetCurrentWeather(gordion);
            if (current == null)
                return;

            List<ProgressionStage> stages = WeatherTweaksCompat.GetStages(current);
            if (stages.Count == 0)
            {
                Plugin.DebugLog(
                    $"'{current.Name}' has no progression stages — the weather stays put for this visit.");
                return;
            }

            _stages = stages;
            _active = true;

            var names = new List<string>(stages.Count);
            foreach (ProgressionStage stage in stages)
                names.Add($"{stage.Name}@{stage.DayTime:0.00}");

            Plugin.Log.LogInfo($"Gordion progression for '{current.Name}': {string.Join(" -> ", names)}.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not set up Gordion weather progression: {e}");
            _active = false;
        }
    }

    public static void Stop()
    {
        _stages = null;
        _nextStage = 0;
        _active = false;
    }

    /// <summary>Applies every stage whose start time the simulated day has passed.</summary>
    public static void Tick(float normalizedTimeOfDay)
    {
        if (!_active || _stages == null)
            return;

        while (_nextStage < _stages.Count && _stages[_nextStage].DayTime <= normalizedTimeOfDay)
        {
            ProgressionStage stage = _stages[_nextStage];
            _nextStage++;

            try
            {
                WeatherController.ChangeCurrentWeather(stage.Weather);
                Plugin.Log.LogInfo(
                    $"Gordion weather progressed to '{stage.Name}' at {normalizedTimeOfDay:0.00} of the day.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not apply progression stage '{stage.Name}': {e.Message}");
            }
        }

        if (_nextStage >= _stages.Count)
            _active = false;
    }

    private static bool IsHost()
    {
        var round = StartOfRound.Instance;
        return round != null && round.IsHost;
    }
}
