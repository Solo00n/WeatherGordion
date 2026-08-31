using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using WeatherGordion.Patches;

namespace WeatherGordion;

/// <summary>
/// Brings weather to 71-Gordion, the one moon the vanilla game keeps permanently clear.
///
/// Two things stand between Gordion and a thunderstorm, and this plugin handles both:
///   1. Its <see cref="SelectableLevel.randomWeathers"/> pool is empty, and WeatherRegistry
///      additionally ships "Company" as the default level filter on every weather, so nothing
///      is ever eligible there. <see cref="WeatherUnlocker"/> fills the pool and assigns weights.
///   2. Gordion has planetHasTime = false, so the day never advances — which is what progressing
///      weather ("Eclipsed > Foggy") needs to switch stages. <see cref="TimeController"/> and
///      <see cref="SimulatedClock"/> offer two different answers to that.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
// Hard: the whole mod is a thin layer on top of WeatherRegistry's weather pool and weights.
// These are BepInPlugin GUIDs, which do not follow the config file names: MrovLib writes
// mrov.MrovLib.cfg but registers itself as plain "MrovLib".
[BepInDependency("mrov.WeatherRegistry")]
[BepInDependency("MrovLib")]
// Soft: combined/progressing weathers come from these, and are picked up by name when present.
[BepInDependency("WeatherTweaks", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("zigzag.combinedweatherstoolkit", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance { get; private set; }
    internal static ManualLogSource Log { get; private set; }
    internal static PluginConfig Cfg { get; private set; }

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Cfg = new PluginConfig(Config);

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll(typeof(StartOfRoundPatches));
        _harmony.PatchAll(typeof(SetPlanetsWeatherPatch));
        _harmony.PatchAll(typeof(RoundManagerPatches));
        _harmony.PatchAll(typeof(TimeOfDayPatches));

        WeatherUnlocker.Subscribe();
        LandingWatcher.Create();

        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded. " +
                    $"Weather on 71-Gordion: {(Cfg.Enabled.Value ? "on" : "off")}, " +
                    $"time mode: {Cfg.TimeMode.Value}.");
    }

    /// <summary>Gated debug logging (see [General] DebugMode).</summary>
    internal static void DebugLog(string message)
    {
        if (Cfg != null && Cfg.DebugMode.Value)
            Log.LogInfo($"[Debug] {message}");
    }

    private void OnDestroy()
    {
        // Gordion's SelectableLevel is a ScriptableObject: a planetHasTime we set and never
        // cleared would outlive this plugin for the rest of the process.
        TimeController.RestoreImmediately();
        WeatherUnlocker.Unsubscribe();
        _harmony?.UnpatchSelf();
    }
}
