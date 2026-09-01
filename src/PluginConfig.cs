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

/// <summary>Per-weather switch and weight, bound under its own <c>[Weather.Name]</c> section.</summary>
internal sealed class WeatherSettings
{
    public ConfigEntry<bool> Enabled;
    public ConfigEntry<int> Weight;

    /// <summary>Weight to apply, or 0 when this weather should not happen on Gordion at all.</summary>
    public int EffectiveWeight => Enabled.Value ? Weight.Value : 0;
}

internal sealed class PluginConfig
{
    /// <summary>
    /// Starting weights for the weathers that ship with the game. Anything not listed here — a modded
    /// weather, or a combination registered by WeatherTweaks or Combined Weathers Toolkit — is bound
    /// switched off, so installing a weather pack never quietly changes what happens at the Company.
    /// </summary>
    private static readonly Dictionary<string, int> DefaultWeights = new Dictionary<string, int>
    {
        ["rainy"] = 120,
        ["foggy"] = 100,
        ["stormy"] = 60,
        ["eclipsed"] = 50,
        ["flooded"] = 40,
        ["dustclouds"] = 60,
    };

    /// <summary>Vanilla weathers that are on out of the box. Dust Clouds is bound off by request.</summary>
    private static readonly HashSet<string> EnabledByDefault = new HashSet<string>
    {
        "rainy", "foggy", "stormy", "eclipsed", "flooded",
    };

    private readonly ConfigFile _file;
    private readonly Dictionary<string, WeatherSettings> _weatherSettings =
        new Dictionary<string, WeatherSettings>(StringComparer.Ordinal);

    // [General]
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> DebugMode;

    // [Weathers]
    public readonly ConfigEntry<int> ClearWeatherWeight;
    public readonly ConfigEntry<bool> RespectExistingConfig;
    public readonly ConfigEntry<string> BannedComponents;

    private HashSet<string> _banned;
    private string _bannedSource;

    // [Time]
    public readonly ConfigEntry<GordionTimeMode> TimeMode;
    public readonly ConfigEntry<bool> FreezeDeadline;
    public readonly ConfigEntry<bool> ShipLeavesAtEndOfDay;
    public readonly ConfigEntry<bool> ShowClock;
    public readonly ConfigEntry<float> DayLengthSeconds;

    public PluginConfig(ConfigFile cfg)
    {
        _file = cfg;

        Enabled = cfg.Bind(
            "1. General", "Enabled", true,
            "Master switch. When off the plugin changes nothing and Gordion stays clear.");

        DebugMode = cfg.Bind(
            "1. General", "DebugMode", false,
            "Verbose logging: which weathers were unlocked, weights written, time transitions.");

        ClearWeatherWeight = cfg.Bind(
            "2. Weathers", "Clear weather weight", 200,
            new ConfigDescription(
                "Weight of clear weather (None) on Gordion, i.e. how often the moon stays as it is in " +
                "vanilla. Set to 0 to guarantee some weather on every visit.",
                new AcceptableValueRange<int>(0, 10000)));

        BannedComponents = cfg.Bind(
            "2. Weathers", "Never allow, even in combinations", "",
            "Semicolon-separated weathers that must never occur on Gordion, including as part of a " +
            "combined or progressing weather. This is stronger than switching a weather off in its own " +
            "section: '[Weather.Rainy] Enabled = false' only removes plain rain, while 'Stormy + Rainy' " +
            "is a separate weather that turns the rain on as well. Listing 'Rainy' here refuses both, " +
            "and every other combination containing rain. Written for exactly that case — rain's " +
            "puddles do not render correctly at the Company. Example: Rainy; Flooded");

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

    /// <summary>Normalised names from <see cref="BannedComponents"/>, rebuilt when the string changes.</summary>
    public HashSet<string> BannedComponentNames()
    {
        string raw = BannedComponents.Value ?? string.Empty;
        if (_banned != null && _bannedSource == raw)
            return _banned;

        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string chunk in raw.Split(';'))
        {
            string name = NormalizeName(chunk);
            if (name.Length > 0)
                parsed.Add(name);
        }

        _banned = parsed;
        _bannedSource = raw;
        return parsed;
    }

    /// <summary>
    /// The switch and weight for one weather, binding its <c>[Weather.Name]</c> section the first time
    /// it is asked for.
    ///
    /// Binding is deferred rather than done in the constructor because the weather list does not exist
    /// yet at that point: WeatherRegistry registers vanilla weathers, and WeatherTweaks and Combined
    /// Weathers Toolkit register their combinations, well after plugins load. The sections therefore
    /// appear in the file once the game has reached the point where those are known — the same way
    /// WeatherRegistry's own per-weather sections do.
    /// </summary>
    public WeatherSettings SettingsFor(string weatherName)
    {
        string key = NormalizeName(weatherName);
        if (_weatherSettings.TryGetValue(key, out WeatherSettings existing))
            return existing;

        string section = "Weather." + SanitizeForConfig(weatherName);
        bool defaultEnabled = EnabledByDefault.Contains(key);
        int defaultWeight = DefaultWeights.TryGetValue(key, out int w) ? w : 100;

        var settings = new WeatherSettings
        {
            Enabled = _file.Bind(
                section, "Enabled", defaultEnabled,
                $"Whether '{weatherName}' can happen on Gordion at all. Turning it off takes it back " +
                "out of the moon's weather pool; nothing else on the moon is affected."),

            Weight = _file.Bind(
                section, "Weight", defaultWeight,
                new ConfigDescription(
                    $"How likely '{weatherName}' is on Gordion, relative to the other weathers here and " +
                    "to 'Clear weather weight'. Ignored while Enabled is false.",
                    new AcceptableValueRange<int>(0, 10000))),
        };

        _weatherSettings[key] = settings;
        return settings;
    }

    /// <summary>
    /// Strips the characters BepInEx refuses inside a config section name. Weather names carry spaces,
    /// '+' and '&gt;' — all fine — but a modded one could contain anything.
    /// </summary>
    private static string SanitizeForConfig(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Unnamed";

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c == '=' || c == '\n' || c == '\t' || c == '\\' || c == '"' || c == '\'' || c == '[' || c == ']')
                continue;
            builder.Append(c);
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "Unnamed" : cleaned;
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
