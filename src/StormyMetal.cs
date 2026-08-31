using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WeatherGordion;

/// <summary>
/// Keeps the storm's list of lightning targets current on Gordion.
///
/// <c>StormyWeather</c> fills its private <c>metalObjects</c> list exactly once, fifteen seconds after
/// the weather turns on, from every conductive <c>GrabbableObject</c> in the scene. On a normal moon
/// that is enough: the scrap is spawned before the scan and stays put. Gordion spawns no scrap at all,
/// and its metal moves constantly — it starts inside the ship, where the targeting loop deliberately
/// skips it (<c>isInShipRoom</c>), then gets carried out, then sold and despawned. A single scan taken
/// at the wrong moment therefore leaves the storm with nothing it is allowed to hit for the whole
/// visit, so the list is rebuilt here on an interval instead.
///
/// Reflection rather than a reference: <c>metalObjects</c> is private, and a rescan that quietly stops
/// working is far better than a hard failure if the field is ever renamed.
/// </summary>
internal sealed class StormyMetal : MonoBehaviour
{
    private const float RescanInterval = 6f;

    private static StormyMetal _instance;
    private static FieldInfo _metalObjectsField;
    private static bool _fieldMissingLogged;

    private float _nextScan;
    private int _lastCount = -1;

    public static void Begin()
    {
        if (_instance != null)
            return;

        var go = new GameObject("WeatherGordion_StormyMetal");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        _instance = go.AddComponent<StormyMetal>();
    }

    public static void Stop()
    {
        if (_instance == null)
            return;

        Destroy(_instance.gameObject);
        _instance = null;
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + RescanInterval;

        try
        {
            Rescan();
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Storm metal rescan failed: {e.Message}");
        }
    }

    private void Rescan()
    {
        if (!GordionLevel.IsCurrent())
            return;

        // Only the host picks lightning targets — StormyWeather's targeting loop returns early on
        // anyone else — so a client rebuilding the list would be a FindObjectsOfType sweep per player
        // for nothing, and would leave the two sides disagreeing about what may be struck.
        var round = StartOfRound.Instance;
        if (round == null || !round.IsServer)
            return;

        var storm = FindObjectOfType<StormyWeather>();
        if (storm == null || !storm.gameObject.activeInHierarchy)
            return;

        _metalObjectsField ??= AccessTools.Field(typeof(StormyWeather), "metalObjects");
        if (_metalObjectsField == null)
        {
            if (!_fieldMissingLogged)
            {
                _fieldMissingLogged = true;
                Plugin.Log.LogWarning(
                    "StormyWeather.metalObjects was not found, so lightning targets cannot be refreshed " +
                    "on Gordion. Storms still work; they just keep whatever targets they found at first.");
            }

            return;
        }

        if (!(_metalObjectsField.GetValue(storm) is List<GrabbableObject> targets))
            return;

        targets.Clear();

        GrabbableObject[] all = FindObjectsOfType<GrabbableObject>();
        foreach (GrabbableObject item in all)
        {
            if (item != null && item.itemProperties != null && item.itemProperties.isConductiveMetal)
                targets.Add(item);
        }

        if (targets.Count != _lastCount)
        {
            _lastCount = targets.Count;
            Plugin.DebugLog($"Storm lightning targets on Gordion: {targets.Count} conductive item(s).");
        }
    }
}
