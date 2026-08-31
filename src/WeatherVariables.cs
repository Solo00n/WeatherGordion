using System.Collections.Generic;
using UnityEngine;
using WeatherRegistry;

namespace WeatherGordion;

/// <summary>
/// Supplies the per-moon weather variables that a weather needs to behave like it does anywhere else.
///
/// <c>RoundManager.SetToCurrentLevelWeather</c> copies <c>weatherVariable</c> and
/// <c>weatherVariable2</c> straight out of the moon's own <c>randomWeathers</c> entry into
/// <c>TimeOfDay.currentWeatherVariable/2</c>, and the weather effects read them from there. They are
/// authored per moon, so an entry injected with zeroes gives a flood that sits at world height 0 and
/// never rises (<c>FloodWeather</c> computes its offset as <c>globalTime / 1080 * variable2</c>) and a
/// fog whose density range is empty.
///
/// Gordion has no authored entries at all, so the values are borrowed from a moon that does define the
/// weather. That keeps the numbers vanilla and version-proof instead of hard-coding magic constants.
/// </summary>
internal static class WeatherVariables
{
    private struct Variables
    {
        public int Variable1;
        public int Variable2;
        public Color Color;
        public string Source;
    }

    private static readonly Dictionary<LevelWeatherType, Variables> Cache =
        new Dictionary<LevelWeatherType, Variables>();

    /// <summary>Drops the cached donors; moon lists differ between sessions and modpack changes.</summary>
    public static void Reset() => Cache.Clear();

    /// <summary>
    /// Fills <paramref name="entry"/> with sensible variables for its weather. Falls back to the
    /// weather effect's authored defaults when no other moon offers the weather.
    /// </summary>
    public static void Apply(RandomWeatherWithVariables entry, Weather weather, SelectableLevel exclude)
    {
        if (entry == null)
            return;

        if (TryFindDonor(entry.weatherType, exclude, out Variables donor))
        {
            entry.weatherVariable = donor.Variable1;
            entry.weatherVariable2 = donor.Variable2;
            entry.weatherVariableColor = donor.Color;
            Plugin.DebugLog(
                $"'{weather?.Name}' variables {donor.Variable1}/{donor.Variable2} borrowed from {donor.Source}.");
            return;
        }

        entry.weatherVariable = weather?.Effect != null ? weather.Effect.DefaultVariable1 : 0;
        entry.weatherVariable2 = weather?.Effect != null ? weather.Effect.DefaultVariable2 : 0;
        Plugin.DebugLog(
            $"'{weather?.Name}': no moon defines this weather, using the effect's own defaults " +
            $"{entry.weatherVariable}/{entry.weatherVariable2}.");
    }

    /// <summary>
    /// True when the entry carries nothing usable — the state an injected entry starts in, and the one
    /// worth replacing once a donor turns up.
    /// </summary>
    public static bool IsUnset(RandomWeatherWithVariables entry)
    {
        return entry != null && entry.weatherVariable == 0 && entry.weatherVariable2 == 0;
    }

    private static bool TryFindDonor(LevelWeatherType type, SelectableLevel exclude, out Variables donor)
    {
        if (Cache.TryGetValue(type, out donor))
            return donor.Source != null;

        donor = default;

        SelectableLevel[] levels = StartOfRound.Instance?.levels;
        if (levels == null)
            return false;

        // Vanilla moons first, and only then anything a mod added. Every player has the vanilla list
        // in the same order, so host and clients pick the same donor and end up with the same flood
        // height and fog density — a preference for whatever came first would hand different numbers
        // to players whose moon mods differ.
        if (TryScan(levels, type, exclude, vanillaOnly: true, out donor)
            || TryScan(levels, type, exclude, vanillaOnly: false, out donor))
        {
            Cache[type] = donor;
            return true;
        }

        Cache[type] = default;
        return false;
    }

    private static bool TryScan(
        SelectableLevel[] levels,
        LevelWeatherType type,
        SelectableLevel exclude,
        bool vanillaOnly,
        out Variables donor)
    {
        donor = default;

        foreach (SelectableLevel level in levels)
        {
            if (level == null || level == exclude || level.randomWeathers == null)
                continue;

            if (vanillaOnly && !IsVanilla(level))
                continue;

            foreach (RandomWeatherWithVariables candidate in level.randomWeathers)
            {
                if (candidate == null || candidate.weatherType != type)
                    continue;

                // Skip another moon's placeholder: it would teach us nothing.
                if (candidate.weatherVariable == 0 && candidate.weatherVariable2 == 0)
                    continue;

                donor = new Variables
                {
                    Variable1 = candidate.weatherVariable,
                    Variable2 = candidate.weatherVariable2,
                    Color = candidate.weatherVariableColor,
                    Source = level.PlanetName ?? level.name,
                };

                return true;
            }
        }

        return false;
    }

    private static bool IsVanilla(SelectableLevel level)
    {
        try
        {
            return MrovLib.LevelHelper.IsVanillaLevel(level);
        }
        catch
        {
            // Without MrovLib's judgement every moon is a candidate; the second scan covers it.
            return false;
        }
    }
}
