using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using WeatherRegistry;

namespace WeatherGordion.Compat;

/// <summary>One stage of a progressing weather: when it starts, and what the weather becomes.</summary>
internal sealed class ProgressionStage
{
    public float DayTime;
    public Weather Weather;
    public string Name;
}

/// <summary>
/// Reads the stage list out of a WeatherTweaks progressing weather.
///
/// Everything here goes through reflection on purpose: WeatherTweaks is a soft dependency, so the mod
/// has to load and work when it is absent or on a version whose types moved. Every lookup failure
/// degrades to "this weather has no stages", which is exactly how a plain combined weather behaves.
/// </summary>
internal static class WeatherTweaksCompat
{
    private static bool _probed;
    private static bool _available;

    private static MethodInfo _getFullWeatherType;   // Variables.GetFullWeatherType(Weather)
    private static Type _progressingType;            // Definitions.ProgressingWeatherType
    private static FieldInfo _weatherEntries;        // ProgressingWeatherType.WeatherEntries
    private static FieldInfo _entryDayTime;          // ProgressingWeatherEntry.DayTime
    private static MethodInfo _entryGetWeather;      // ProgressingWeatherEntry.GetWeather()
    private static PropertyInfo _weatherTypes;       // WeatherTweaksWeather.WeatherTypes

    /// <summary>True when WeatherTweaks is loaded and its progressing-weather types were found.</summary>
    public static bool Available
    {
        get
        {
            Probe();
            return _available;
        }
    }

    /// <summary>
    /// Stages of <paramref name="weather"/> in ascending time order, or an empty list when it is not a
    /// progressing weather (a plain or combined weather simply has nothing to step through).
    /// </summary>
    public static List<ProgressionStage> GetStages(Weather weather)
    {
        var stages = new List<ProgressionStage>();
        if (weather == null || !Available)
            return stages;

        try
        {
            object full = _getFullWeatherType.Invoke(null, new object[] { weather });
            if (full == null || !_progressingType.IsInstanceOfType(full))
                return stages;

            if (!(_weatherEntries.GetValue(full) is IEnumerable entries))
                return stages;

            foreach (object entry in entries)
            {
                if (entry == null)
                    continue;

                float dayTime = (float)_entryDayTime.GetValue(entry);
                var stageWeather = _entryGetWeather.Invoke(entry, null) as Weather;
                if (stageWeather == null)
                    continue;

                stages.Add(new ProgressionStage
                {
                    DayTime = dayTime,
                    Weather = stageWeather,
                    Name = stageWeather.Name,
                });
            }

            stages.Sort((a, b) => a.DayTime.CompareTo(b.DayTime));
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Could not read progression stages for '{weather.Name}': {e.Message}");
            stages.Clear();
        }

        return stages;
    }

    /// <summary>
    /// The weathers <paramref name="weather"/> is made of, as they would be applied on the moon.
    ///
    /// A combination is its own <c>Weather</c> object with its own name, so switching off
    /// <c>[Weather.Rainy]</c> says nothing about "Stormy + Rainy" — that is a different weather that
    /// happens to turn the rain on as well. This is what lets a component be refused wherever it
    /// appears. A weather that is not a WeatherTweaks combination reports only itself.
    /// </summary>
    public static List<string> GetComponentNames(Weather weather)
    {
        var parts = new List<string>();
        if (weather == null)
            return parts;

        parts.Add(weather.VanillaWeatherType.ToString());
        parts.Add(weather.Name);

        foreach (WeatherResolvable resolvable in ReadResolvables(weather))
        {
            // Both spellings: a resolvable may carry a name that never resolved to a type, and a type
            // whose name was never set. Either one is enough to recognise the component.
            try
            {
                parts.Add(resolvable.WeatherType.ToString());
            }
            catch
            {
                // An unresolvable entry still has its name below.
            }

            try
            {
                if (!string.IsNullOrEmpty(resolvable.WeatherName))
                    parts.Add(resolvable.WeatherName);
            }
            catch
            {
                // Nothing more to read from this one.
            }
        }

        return parts;
    }

    /// <summary>
    /// The component list of a WeatherTweaks weather.
    ///
    /// Read straight off the object where possible: a combination *is* a
    /// <c>WeatherTweaksWeather</c>, which derives from <c>WeatherRegistry.Weather</c>, so the property
    /// is right there. Going through <c>Variables.GetFullWeatherType</c> would add a lookup that can
    /// come back empty and quietly cost us the components — and with them the only way to recognise a
    /// combination like "The Great Flood", whose name says nothing about the rain inside it.
    /// </summary>
    private static IEnumerable<WeatherResolvable> ReadResolvables(Weather weather)
    {
        if (!Available)
            yield break;

        object source = null;

        PropertyInfo direct = weather.GetType().GetProperty(
            "WeatherTypes", BindingFlags.Public | BindingFlags.Instance);
        if (direct != null)
        {
            source = SafeGet(direct, weather);
        }
        else if (_getFullWeatherType != null && _weatherTypes != null)
        {
            object full = SafeInvoke(_getFullWeatherType, new object[] { weather });
            if (full != null)
                source = SafeGet(_weatherTypes, full);
        }

        if (!(source is IEnumerable items))
            yield break;

        foreach (object item in items)
        {
            if (item is WeatherResolvable resolvable)
                yield return resolvable;
        }
    }

    private static object SafeGet(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Reading {property.Name} failed: {e.Message}");
            return null;
        }
    }

    private static object SafeInvoke(MethodInfo method, object[] args)
    {
        try
        {
            return method.Invoke(null, args);
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Calling {method.Name} failed: {e.Message}");
            return null;
        }
    }

    private static void Probe()
    {
        if (_probed)
            return;
        _probed = true;

        try
        {
            Type variables = FindType("WeatherTweaks.Variables");
            _progressingType = FindType("WeatherTweaks.Definitions.ProgressingWeatherType");
            Type entryType = FindType("WeatherTweaks.Definitions.ProgressingWeatherEntry");
            Type tweaksWeather = FindType("WeatherTweaks.Definitions.WeatherTweaksWeather");
            _weatherTypes = tweaksWeather?.GetProperty(
                "WeatherTypes", BindingFlags.Public | BindingFlags.Instance);

            if (variables == null || _progressingType == null || entryType == null)
            {
                Plugin.DebugLog("WeatherTweaks not found — progressing weather stages are unavailable.");
                return;
            }

            _getFullWeatherType = variables.GetMethod(
                "GetFullWeatherType",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Weather) },
                null);

            _weatherEntries = _progressingType.GetField(
                "WeatherEntries", BindingFlags.Public | BindingFlags.Instance);

            _entryDayTime = entryType.GetField("DayTime", BindingFlags.Public | BindingFlags.Instance);
            _entryGetWeather = entryType.GetMethod(
                "GetWeather", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            _available = _getFullWeatherType != null
                         && _weatherEntries != null
                         && _entryDayTime != null
                         && _entryGetWeather != null;

            if (_available)
                Plugin.DebugLog("WeatherTweaks progressing-weather support is available.");
            else
                Plugin.Log.LogWarning(
                    "WeatherTweaks is installed but its progressing-weather API did not match what this " +
                    "mod expects; stages will not be stepped through in Simulated mode.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"WeatherTweaks compatibility probe failed: {e.Message}");
            _available = false;
        }
    }

    private static Type FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }
            catch
            {
                // Dynamic or unloadable assembly; skip it.
            }
        }

        return null;
    }
}
