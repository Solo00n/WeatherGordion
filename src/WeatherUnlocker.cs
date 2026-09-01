using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using WeatherGordion.Compat;
using WeatherRegistry;
using WeatherRegistry.Enums;

namespace WeatherGordion;

/// <summary>
/// Makes the configured weathers eligible on Gordion.
///
/// Selection reads the moon's <see cref="SelectableLevel.randomWeathers"/> pool, and Gordion's is
/// empty. Clearing the moon filter is not enough to fill it: WeatherRegistry only ever *injects* an
/// entry for a modded weather, and for a vanilla one it defers to the moon creator — "Vanilla weather
/// not defined by moon creator" — which for Gordion means nothing at all. So the pool is written here
/// directly, right before every selection.
///
/// The filter still has to be corrected, for a different reason: WeatherRegistry's setup pass actively
/// strips weathers off the company moon unless the moon is in that weather's apply-list ("Removed from
/// company moon"), and every weather ships with <c>Level filter = Company;</c> under a blacklist
/// because <c>Defaults.DefaultLevelFilters</c> is the literal string "Company". Fixing it is what makes
/// the change survive a lobby reload.
///
/// Note that <c>Weather.LevelFilters</c> and <c>Weather.LevelWeights</c> hand out freshly built copies
/// (<c>Config.LevelFilters.Value.ToList()</c>), so mutating what they return — including through
/// WeatherRegistry's own <c>Weather.RemoveFromMoon</c>, which is a no-op for exactly this reason —
/// changes nothing. The only thing that sticks is the config handler's backing entry.
/// </summary>
internal static class WeatherUnlocker
{
    /// <summary>Weathers this mod added to Gordion's pool, so it can take them back out again.</summary>
    private static readonly HashSet<LevelWeatherType> Added = new HashSet<LevelWeatherType>();

    /// <summary>
    /// Weathers that already carried a Gordion weight before this mod ever wrote one, captured once
    /// per process. Comparing against the live value instead would be self-defeating: after the first
    /// pass our own weight looks hand-tuned, and "Respect existing config" would then stop the mod
    /// from ever applying an edited weight. Deliberately not cleared by <see cref="Reset"/> — a new
    /// lobby does not restore the file, so the honest snapshot is the first one.
    /// </summary>
    private static readonly HashSet<string> PreExistingWeights = new HashSet<string>(StringComparer.Ordinal);

    private static bool _snapshotTaken;
    private static bool _subscribed;
    private static bool _loggedSummary;

    public static void Subscribe()
    {
        if (_subscribed)
            return;

        try
        {
            EventManager.SetupFinished.AddListener(OnSetupFinished);
            EventManager.DayChanged.AddListener(OnDayChanged);
            _subscribed = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(
                $"Could not subscribe to WeatherRegistry events: {e.Message}. Gordion weather will " +
                "still be applied from the SetPlanetsWeather patch.");
        }
    }

    public static void Unsubscribe()
    {
        if (!_subscribed)
            return;

        try
        {
            EventManager.SetupFinished.RemoveListener(OnSetupFinished);
            EventManager.DayChanged.RemoveListener(OnDayChanged);
        }
        catch
        {
            // WeatherRegistry is going away too; nothing useful to log.
        }

        _subscribed = false;
    }

    private static void OnSetupFinished() => Apply("WeatherRegistry setup finished");

    private static void OnDayChanged(int day) => Apply($"day {day} started");

    /// <summary>Forgets what was applied, so a fresh session re-adds everything from scratch.</summary>
    public static void Reset()
    {
        Added.Clear();
        WeatherVariables.Reset();
        _loggedSummary = false;
    }

    /// <summary>
    /// Brings Gordion's weather pool and weights in line with the config. Idempotent and cheap enough
    /// to run before every weather selection.
    /// </summary>
    public static void Apply(string reason)
    {
        if (Plugin.Cfg == null || !Plugin.Cfg.Enabled.Value)
            return;

        SelectableLevel gordion = GordionLevel.Level;
        if (gordion == null)
            return;

        try
        {
            ApplyCore(gordion, reason);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to unlock weather on Gordion ({reason}): {e}");
        }
    }

    private static void ApplyCore(SelectableLevel gordion, string reason)
    {
        List<Weather> weathers = WeatherManager.Weathers;
        if (weathers == null || weathers.Count == 0)
        {
            Plugin.DebugLog($"No weathers registered yet ({reason}); nothing to unlock.");
            return;
        }

        TakeSnapshot(gordion, weathers);

        var applied = new List<string>();
        var disabled = new List<string>();
        var banned = new List<string>();

        foreach (Weather weather in weathers)
        {
            if (weather == null)
                continue;

            // Clear weather gets no switch: the selection always offers it, so all it needs is the
            // weight that decides how often Gordion simply stays as it is.
            if (weather.Type == WeatherType.Clear)
            {
                SetLevelWeight(weather, gordion, Plugin.Cfg.ClearWeatherWeight.Value);
                continue;
            }

            int weight = Plugin.Cfg.SettingsFor(weather.Name).EffectiveWeight;

            // A ban outranks the weather's own switch: it exists precisely to catch the combinations
            // whose sections still say Enabled, because they were never the thing being turned off.
            if (IsBanned(weather))
            {
                RemoveFromPool(gordion, weather);
                banned.Add(weather.Name);
                continue;
            }

            if (weight == 0)
            {
                RemoveFromPool(gordion, weather);
                disabled.Add(weather.Name);
                continue;
            }

            if (Plugin.Cfg.RespectExistingConfig.Value && HasHandTunedWeight(weather))
            {
                Plugin.DebugLog(
                    $"'{weather.Name}' already has a Gordion weight in mrov.WeatherRegistry.cfg — left alone.");
                AddToPool(gordion, weather);
                applied.Add($"{weather.Name} (kept existing weight)");
                continue;
            }

            AllowOnLevel(weather, gordion);
            SetLevelWeight(weather, gordion, weight);
            AddToPool(gordion, weather);
            applied.Add($"{weather.Name}@{weight}");
        }

        if (!_loggedSummary || Plugin.Cfg.DebugMode.Value)
        {
            _loggedSummary = true;
            Plugin.Log.LogInfo(
                $"Gordion weather pool ({reason}): {(applied.Count > 0 ? string.Join(", ", applied) : "nothing")}" +
                $"; clear weight {Plugin.Cfg.ClearWeatherWeight.Value}.");

            // Logged at info, not debug: a weather whose own section still says Enabled but which never
            // shows up is exactly the thing worth being able to explain without turning on debugging.
            if (banned.Count > 0)
                Plugin.Log.LogInfo(
                    $"Refused by 'Never allow, even in combinations' ({Plugin.Cfg.BannedComponents.Value}): " +
                    $"{string.Join(", ", banned)}.");

            if (disabled.Count > 0)
                Plugin.DebugLog($"Switched off for Gordion: {string.Join(", ", disabled)}.");
        }
    }

    /// <summary>
    /// True when the weather is one the config refuses outright, or is built from one.
    ///
    /// A combination is its own weather with its own name, so switching <c>[Weather.Rainy]</c> off says
    /// nothing about "Stormy + Rainy" — that one turns the rain on too. Checking the components is what
    /// makes a ban hold everywhere the weather can appear.
    /// </summary>
    private static bool IsBanned(Weather weather)
    {
        HashSet<string> banned = Plugin.Cfg.BannedComponentNames();
        if (banned.Count == 0)
            return false;

        foreach (LevelWeatherType component in WeatherTweaksCompat.GetComponents(weather))
        {
            if (banned.Contains(PluginConfig.NormalizeName(component.ToString())))
                return true;
        }

        // Last resort for a weather whose parts cannot be read: match the words in its own name, so
        // "Stormy + Rainy" is still caught when WeatherTweaks is absent or has moved its types.
        foreach (string part in (weather.Name ?? string.Empty).Split('+', '>'))
        {
            if (banned.Contains(PluginConfig.NormalizeName(part)))
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- level filter

    /// <summary>
    /// Takes Gordion out of a blacklist, or puts it into a non-empty whitelist.
    ///
    /// This does not add the weather by itself — <see cref="AddToPool"/> does that. What it buys is
    /// survival: WeatherRegistry's setup pass strips every weather from the company moon unless the
    /// moon is in that weather's apply-list ("Removed from company moon" in its log), so without this
    /// a lobby reload would quietly undo the work.
    ///
    /// A whitelist that is empty is left alone on purpose. Empty means the weather is applied to no
    /// levels at all, so putting Gordion in would read as "only ever on Gordion". Every WeatherTweaks
    /// combined weather is configured that way and is handled by WeatherTweaks' own algorithm rather
    /// than this list, so editing it would risk the rest of the moons for no gain here.
    /// </summary>
    private static void AllowOnLevel(Weather weather, SelectableLevel level)
    {
        var handler = weather.Config?.LevelFilters;
        if (handler == null)
            return;

        bool include = weather.LevelFilteringOption == FilteringOption.Include;
        SelectableLevel[] current = handler.Value ?? Array.Empty<SelectableLevel>();

        if (include && current.Length == 0)
            return; // No filtering in effect — Gordion is already allowed.

        bool listed = current.Contains(level);
        if (include == listed)
            return; // Whitelisted already, or not blacklisted — either way nothing to do.

        IEnumerable<SelectableLevel> updated = include
            ? current.Concat(new[] { level })
            : current.Where(l => l != level);

        string serialised = SerialiseLevels(updated);
        if (!WriteHandler(handler, serialised))
            return;

        Plugin.DebugLog(
            $"'{weather.Name}': {(include ? "added Gordion to" : "removed Gordion from")} its level " +
            $"filter -> '{serialised}'.");
    }

    // ---------------------------------------------------------------- level weight

    /// <summary>
    /// Records which weathers carried a Gordion weight before this mod wrote anything. Runs once for
    /// the lifetime of the process, on the first pass that sees a populated weather list.
    /// </summary>
    private static void TakeSnapshot(SelectableLevel gordion, List<Weather> weathers)
    {
        if (_snapshotTaken)
            return;
        _snapshotTaken = true;

        foreach (Weather weather in weathers)
        {
            if (weather != null && CarriesWeightFor(weather, gordion))
                PreExistingWeights.Add(weather.Name ?? string.Empty);
        }

        if (PreExistingWeights.Count > 0)
            Plugin.DebugLog(
                $"Already had a Gordion weight in mrov.WeatherRegistry.cfg: {string.Join(", ", PreExistingWeights)}.");
    }

    /// <summary>True when the user gave this weather a Gordion weight themselves, before this mod ran.</summary>
    private static bool HasHandTunedWeight(Weather weather)
    {
        return PreExistingWeights.Contains(weather.Name ?? string.Empty);
    }

    /// <summary>True when the weather's level weights currently mention <paramref name="level"/>.</summary>
    private static bool CarriesWeightFor(Weather weather, SelectableLevel level)
    {
        var handler = weather.Config?.LevelWeights;
        LevelRarity[] current = handler?.Value;
        if (current == null)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != null && current[i].Level == level)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Writes <c>Gordion@weight</c> into the weather's level weights, keeping every other moon's entry
    /// exactly as the user left it.
    /// </summary>
    private static void SetLevelWeight(Weather weather, SelectableLevel level, int weight)
    {
        var handler = weather.Config?.LevelWeights;
        if (handler == null)
            return;

        if (Plugin.Cfg.RespectExistingConfig.Value && HasHandTunedWeight(weather))
            return;

        LevelRarity[] current = handler.Value ?? Array.Empty<LevelRarity>();
        string key = GordionLevel.ConfigKey(level);

        var parts = new List<string>();
        bool replaced = false;

        foreach (LevelRarity rarity in current)
        {
            if (rarity?.Level == null)
                continue;

            if (rarity.Level == level)
            {
                if (replaced)
                    continue;
                parts.Add($"{key}@{weight}");
                replaced = true;
            }
            else
            {
                parts.Add($"{GordionLevel.ConfigKey(rarity.Level)}@{rarity.Weight}");
            }
        }

        if (!replaced)
            parts.Add($"{key}@{weight}");

        string serialised = string.Join(";", parts) + ";";
        if (WriteHandler(handler, serialised))
            Plugin.DebugLog($"'{weather.Name}': level weights -> '{serialised}'.");
    }

    // ---------------------------------------------------------------- weather pool

    /// <summary>
    /// Puts the weather into Gordion's <see cref="SelectableLevel.randomWeathers"/>, which is the list
    /// the selection algorithm actually reads.
    /// </summary>
    private static void AddToPool(SelectableLevel level, Weather weather)
    {
        // Note what is already there is NOT recorded as ours: if another mod put this weather on
        // Gordion, setting its weight to 0 here must not take away that mod's entry.
        RandomWeatherWithVariables existing = FindInPool(level, weather.VanillaWeatherType);
        if (existing != null)
        {
            // An entry this mod added before WeatherVariables existed, or one added before the moon
            // list was ready, carries zeroes. Fill it in rather than leaving a flood that never rises.
            if (Added.Contains(weather.VanillaWeatherType) && WeatherVariables.IsUnset(existing))
                WeatherVariables.Apply(existing, weather, level);

            return;
        }

        var entry = new RandomWeatherWithVariables { weatherType = weather.VanillaWeatherType };
        WeatherVariables.Apply(entry, weather, level);

        WeatherController.AddRandomWeather(level, entry);
        Added.Add(entry.weatherType);
        Plugin.DebugLog(
            $"'{weather.Name}' added to Gordion's weather pool " +
            $"(variables {entry.weatherVariable}/{entry.weatherVariable2}).");
    }

    /// <summary>Takes back a weather this mod added; never touches one that was already there.</summary>
    private static void RemoveFromPool(SelectableLevel level, Weather weather)
    {
        LevelWeatherType type = weather.VanillaWeatherType;
        if (!Added.Contains(type) || !IsInPool(level, type))
            return;

        WeatherController.RemoveRandomWeather(level, type);
        Added.Remove(type);
        Plugin.DebugLog($"'{weather.Name}' removed from Gordion's weather pool (configured weight 0).");
    }

    private static bool IsInPool(SelectableLevel level, LevelWeatherType type)
    {
        return FindInPool(level, type) != null;
    }

    private static RandomWeatherWithVariables FindInPool(SelectableLevel level, LevelWeatherType type)
    {
        RandomWeatherWithVariables[] pool = level.randomWeathers;
        if (pool == null)
            return null;

        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i] != null && pool[i].weatherType == type)
                return pool[i];
        }

        return null;
    }

    // ---------------------------------------------------------------- handler plumbing

    /// <summary>
    /// Pushes a new raw value into a WeatherRegistry config handler.
    ///
    /// The handler recomputes <c>Value</c> from its BepInEx entry on every access, so writing the entry
    /// is what makes the change take. BepInEx would normally flush the whole file on assignment, which
    /// would rewrite the user's mrov.WeatherRegistry.cfg behind their back — so saving is switched off
    /// for the duration of the write and the change stays in memory only.
    /// </summary>
    private static bool WriteHandler<TValue>(
        WeatherRegistry.Utils.ConfigHandler<TValue, string> handler, string raw)
    {
        try
        {
            // Mirror exactly what the handler's Value getter reads: the bound entry only when it is
            // active, and the default otherwise. Writing to a bound-but-inactive entry would look like
            // it worked and change nothing.
            if (!handler.ConfigEntryActive)
            {
                if (handler.DefaultValue == raw)
                    return false;
                handler.DefaultValue = raw;
                return true;
            }

            ConfigEntry<string> entry = handler.ConfigEntry;
            if (entry.Value == raw)
                return false;

            ConfigFile file = entry.ConfigFile;
            bool previous = file != null && file.SaveOnConfigSet;
            if (file != null)
                file.SaveOnConfigSet = false;

            try
            {
                entry.Value = raw;
            }
            finally
            {
                if (file != null)
                    file.SaveOnConfigSet = previous;
            }

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not write a WeatherRegistry config handler: {e.Message}");
            return false;
        }
    }

    private static string SerialiseLevels(IEnumerable<SelectableLevel> levels)
    {
        var names = levels
            .Where(l => l != null)
            .Select(GordionLevel.ConfigKey)
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        return names.Count == 0 ? string.Empty : string.Join(";", names) + ";";
    }
}
