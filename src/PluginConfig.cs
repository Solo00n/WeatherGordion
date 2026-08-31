using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace WeatherGordion;

/// <summary>How the day is made to pass on Gordion, which normally has no day cycle at all.</summary>
internal enum GordionTimeMode
{
    /// <summary>Don't touch time. Weather is picked on landing and stays for the whole visit.</summary>
    Off,

    /// <summary>
    /// Start the game's own clock while landed. Progressing weather, the rising Flooded water and the
    /// storm's random strikes all work, because all three are driven by the real TimeOfDay.
    /// planetHasTime is left alone; see <see cref="TimeController"/> for why.
    /// </summary>
    RealTime,

    /// <summary>
    /// Leave planetHasTime alone and run our own clock instead, driving weather stages and the
    /// HUD clock by hand. Touches nothing else in the game.
    /// </summary>
    Simulated,
}

internal sealed class PluginConfig
{
    // [General]
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> DebugMode;

    // [Weathers]
    public readonly ConfigEntry<string> WeatherWeights;
    public readonly ConfigEntry<int> ClearWeatherWeight;
    public readonly ConfigEntry<bool> RespectExistingConfig;

    // [Time]
    public readonly ConfigEntry<GordionTimeMode> TimeMode;
    public readonly ConfigEntry<bool> FreezeDeadline;
    public readonly ConfigEntry<bool> ShipLeavesAtEndOfDay;
    public readonly ConfigEntry<bool> ShowClock;
    public readonly ConfigEntry<float> DayLengthSeconds;

    /// <summary>Parsed <see cref="WeatherWeights"/>, rebuilt whenever the raw string changes.</summary>
    private Dictionary<string, int> _weights;
    private string _weightsSource;

    public PluginConfig(ConfigFile cfg)
    {
        Enabled = cfg.Bind(
            "1. General", "Enabled", true,
            "Master switch. When off the plugin changes nothing and Gordion stays clear.");

        DebugMode = cfg.Bind(
            "1. General", "DebugMode", false,
            "Verbose logging: which weathers were unlocked, weights written, time transitions.");

        WeatherWeights = cfg.Bind(
            "2. Weathers", "Weather weights",
            "Rainy@120; Foggy@100; Stormy@60; Flooded@40; Eclipsed@50",
            "Weathers allowed on Gordion and how likely each one is, written as Name@Weight and " +
            "separated by semicolons. Names are the ones WeatherRegistry uses for its config section " +
            "titles, so combined and progressing weathers work too: Stormy + Rainy@40; Eclipsed > Foggy@20. " +
            "Weight 0 removes a weather this mod added earlier. Weights are relative to each other and " +
            "to 'Clear weather weight'.");

        ClearWeatherWeight = cfg.Bind(
            "2. Weathers", "Clear weather weight", 200,
            new ConfigDescription(
                "Weight of clear weather (None) on Gordion, i.e. how often the moon stays as it is in " +
                "vanilla. Set to 0 to guarantee some weather on every visit.",
                new AcceptableValueRange<int>(0, 10000)));

        RespectExistingConfig = cfg.Bind(
            "2. Weathers", "Respect existing config", true,
            "Leave a weather alone if it already has a Gordion weight in mrov.WeatherRegistry.cfg. " +
            "Keeps hand-tuned entries (for example an Eclipsed you unblocked yourself) from being " +
            "overwritten by this mod's defaults.");

        // Deliberately no scrap-multiplier setting here: WeatherRegistry's multipliers belong to the
        // weather, not to the moon, so they already apply on Gordion the moment a weather is active
        // there. See the README for why that stays invisible in practice.

        TimeMode = cfg.Bind(
            "3. Time", "Gordion time mode", GordionTimeMode.RealTime,
            "Off       - no day cycle; weather is fixed for the whole visit (vanilla behaviour).\n" +
            "RealTime  - start the game's own clock on Gordion while landed, so progressing weather, " +
            "the rising Flooded water and the HUD clock all work. planetHasTime is deliberately left " +
            "alone, so the ship still never leaves on its own, no end-of-round stats screen appears, " +
            "and landing late in the day is not blocked.\n" +
            "Simulated - never touch the game clock at all; drive weather stages and the HUD clock " +
            "from this mod's own timer instead. Use it if RealTime upsets another mod.");

        FreezeDeadline = cfg.Bind(
            "3. Time", "Freeze deadline on Gordion", true,
            "RealTime only. The vanilla clock drains the quota deadline as it runs, which on Gordion " +
            "would both cost you days and move the company buying rate — neither happens in vanilla, " +
            "where the clock is stopped. Leave this on to hold the deadline still for the visit.");

        ShipLeavesAtEndOfDay = cfg.Bind(
            "3. Time", "Ship leaves at end of day", true,
            "RealTime only. Let the ship fly off on its own when the Gordion day runs out, exactly as " +
            "it does on every other moon, with the usual warning at 90% of the day. Vanilla cannot do " +
            "this at the Company because the whole midnight-departure branch sits behind planetHasTime, " +
            "so the mod calls the game's own networked departure itself. Turn it off to keep leaving " +
            "manual and never risk being cut off mid-sale.");

        ShowClock = cfg.Bind(
            "3. Time", "Show clock on Gordion", true,
            "Show the HUD clock while on Gordion, indoors included. Vanilla hides the clock whenever " +
            "you count as inside, which at the Company is most of the visit, so the mod asserts it " +
            "instead. Works in both RealTime and Simulated.");

        DayLengthSeconds = cfg.Bind(
            "3. Time", "Day length seconds", 1200f,
            new ConfigDescription(
                "Simulated only: real seconds for a full Gordion day, i.e. how long progressing weather " +
                "takes to run through all of its stages.",
                new AcceptableValueRange<float>(60f, 7200f)));
    }

    /// <summary>
    /// Configured weathers as name -> weight. Keys are normalised with <see cref="NormalizeName"/>,
    /// so "Stormy + Rainy", "stormy+rainy" and "STORMY  +  RAINY" all land on the same entry.
    /// </summary>
    public Dictionary<string, int> WeatherWeightMap()
    {
        string raw = WeatherWeights.Value ?? string.Empty;
        if (_weights != null && _weightsSource == raw)
            return _weights;

        var parsed = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string chunk in raw.Split(';'))
        {
            string entry = chunk.Trim();
            if (entry.Length == 0)
                continue;

            // Weather names contain '+' and '>' but never '@', so the LAST '@' separates name
            // from weight and "Stormy + Rainy@40" parses the way it reads.
            int at = entry.LastIndexOf('@');
            if (at <= 0)
            {
                Plugin.Log.LogWarning($"[Weathers] Ignoring '{entry}': expected the form Name@Weight.");
                continue;
            }

            string name = NormalizeName(entry.Substring(0, at));
            string weightText = entry.Substring(at + 1).Trim();

            if (name.Length == 0)
            {
                Plugin.Log.LogWarning($"[Weathers] Ignoring '{entry}': the weather name is empty.");
                continue;
            }

            if (!int.TryParse(weightText, out int weight) || weight < 0)
            {
                Plugin.Log.LogWarning(
                    $"[Weathers] Ignoring '{entry}': '{weightText}' is not a weight of 0 or more.");
                continue;
            }

            parsed[name] = weight;
        }

        _weights = parsed;
        _weightsSource = raw;
        return parsed;
    }

    /// <summary>
    /// Folds away everything that only varies cosmetically between how a weather is written in this
    /// config and how WeatherRegistry names it: letter case and the spacing around '+' and '>'.
    /// </summary>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c == ' ' || c == '\t' || c == '-' || c == '_')
                continue;
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
