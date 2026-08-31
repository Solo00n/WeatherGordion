using System;
using UnityEngine;

namespace WeatherGordion;

/// <summary>
/// Finds and remembers the Company building moon.
///
/// The usual way to identify it — <c>!planetHasTime &amp;&amp; !spawnEnemiesAndScrap</c>, which is what
/// MrovLib and FirstDayGordion both use — stops working the moment this mod turns time on in
/// RealTime mode. So the level is resolved exactly once per session, at StartOfRound.Awake, while
/// the flags are still untouched, and every later lookup goes through the cached reference.
/// </summary>
internal static class GordionLevel
{
    /// <summary>Names that mean "the vanilla Company building" when nothing matched by name.</summary>
    private static readonly string[] CompanyAliases = { "gordion", "71gordion", "companybuilding", "company" };

    private static SelectableLevel _level;

    /// <summary>The Company moon for this session, or null before StartOfRound exists.</summary>
    public static SelectableLevel Level => _level;

    /// <summary>Value of planetHasTime as the game shipped it, captured before we touch anything.</summary>
    public static bool OriginalPlanetHasTime { get; private set; }

    /// <summary>
    /// Resolves the Company moon from <paramref name="round"/> and caches it. Safe to call more than
    /// once; a level found earlier in the same session is kept.
    /// </summary>
    public static void Resolve(StartOfRound round)
    {
        if (_level != null)
            return;

        SelectableLevel[] levels = round?.levels;
        if (levels == null || levels.Length == 0)
            return;

        SelectableLevel found = FindByName(levels) ?? FindByShape(levels);
        if (found == null)
        {
            Plugin.Log.LogWarning(
                "Could not find the Company building among the moons — weather on Gordion is disabled " +
                "for this session. If a moon loader renamed it, that is the likely cause.");
            return;
        }

        _level = found;
        OriginalPlanetHasTime = found.planetHasTime;

        Plugin.DebugLog(
            $"Company moon resolved: '{found.PlanetName}' (scene '{found.sceneName}', levelID {found.levelID}, " +
            $"planetHasTime {found.planetHasTime}, randomWeathers {found.randomWeathers?.Length ?? 0}).");
    }

    /// <summary>Drops the cached level. Called when StartOfRound goes away (menu, disconnect).</summary>
    public static void Reset()
    {
        _level = null;
    }

    /// <summary>True when <paramref name="level"/> is the Company moon we resolved this session.</summary>
    public static bool Is(SelectableLevel level) => level != null && ReferenceEquals(level, _level);

    /// <summary>True when the ship is currently at Gordion (landed or not).</summary>
    public static bool IsCurrent()
    {
        var round = StartOfRound.Instance;
        return round != null && Is(round.currentLevel);
    }

    /// <summary>Displayed name, with the leading catalogue number stripped: "71 Gordion" -> "gordion".</summary>
    private static SelectableLevel FindByName(SelectableLevel[] levels)
    {
        foreach (string alias in CompanyAliases)
        {
            for (int i = 0; i < levels.Length; i++)
            {
                SelectableLevel level = levels[i];
                if (level == null)
                    continue;

                if (Normalize(level.PlanetName) == alias
                    || Normalize(level.sceneName) == alias
                    || Normalize(level.name) == alias)
                    return level;
            }
        }

        return null;
    }

    /// <summary>
    /// Fallback for a renamed moon list (Celestial Tint, translations, LethalLevelLoader): the only
    /// moon shape the Company can have is no day cycle and nothing spawning on it.
    /// </summary>
    private static SelectableLevel FindByShape(SelectableLevel[] levels)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            SelectableLevel level = levels[i];
            if (level != null && !level.planetHasTime && !level.spawnEnemiesAndScrap)
            {
                Plugin.Log.LogInfo(
                    $"No moon matched a Company name; using '{level.PlanetName}' (no day cycle, nothing " +
                    "spawns) as the Company building.");
                return level;
            }
        }

        return null;
    }

    /// <summary>
    /// Lower-cases a moon name and drops everything that only ever varies cosmetically: the leading
    /// catalogue number and any spaces, dashes and underscores. "71-Gordion", "71 Gordion" and
    /// "gordion" all collapse to the same key.
    /// </summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        int start = 0;
        while (start < name.Length && char.IsDigit(name[start]))
            start++;

        // Only treat the digits as a catalogue number if something is left after them.
        if (start >= name.Length)
            start = 0;

        var builder = new System.Text.StringBuilder(name.Length - start);
        for (int i = start; i < name.Length; i++)
        {
            char c = name[i];
            if (c == ' ' || c == '-' || c == '_')
                continue;
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The key WeatherRegistry expects for this level inside its semicolon-separated config lists.
    /// MrovLib builds its lookup table from, among others, the planet name with the catalogue number
    /// and separators stripped — which is what <c>GetAlphanumericName</c> produces.
    /// </summary>
    public static string ConfigKey(SelectableLevel level)
    {
        if (level == null)
            return string.Empty;

        try
        {
            string key = MrovLib.LevelHelper.GetAlphanumericName(level);
            if (!string.IsNullOrEmpty(key))
                return key;
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"MrovLib.LevelHelper.GetAlphanumericName failed ({e.Message}); falling back.");
        }

        // Same transformation by hand: drop a leading number and the separator characters.
        string name = level.PlanetName ?? level.name ?? string.Empty;
        var builder = new System.Text.StringBuilder(name.Length);
        int start = 0;
        while (start < name.Length && char.IsDigit(name[start]))
            start++;
        if (start >= name.Length)
            start = 0;
        for (int i = start; i < name.Length; i++)
        {
            char c = name[i];
            if (c == ' ' || c == '-' || c == '_' || c == '/' || c == '\\')
                continue;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
