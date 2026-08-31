using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WeatherGordion;

/// <summary>
/// Drives the time-mode lifecycle from ship state rather than from a Harmony hook into the landing
/// coroutine. With a large modpack that coroutine is a popular transpiler target and a postfix there
/// can be starved, so this polls the public flags twice a second instead — the same approach that
/// proved reliable in MonstersGordion.
/// </summary>
internal sealed class LandingWatcher : MonoBehaviour
{
    private const float PollInterval = 0.5f;

    /// <summary>
    /// If another mod breaks the doors-opening sequence, shipHasLanded may never be set even though
    /// we are standing on Gordion. After this long we treat the ship as landed anyway.
    /// </summary>
    private const float FallbackLandedAfter = 20f;

    private float _nextPoll;
    private bool _landedHandled;
    private float _fallbackTimer;

    internal static void Create()
    {
        var go = new GameObject("WeatherGordion_LandingWatcher");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<LandingWatcher>();
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextPoll)
            return;
        _nextPoll = Time.unscaledTime + PollInterval;

        try
        {
            Poll();
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"LandingWatcher poll failed: {e.Message}");
        }
    }

    private void Poll()
    {
        var round = StartOfRound.Instance;
        if (round == null)
        {
            // Main menu or disconnected.
            if (_landedHandled)
            {
                _landedHandled = false;
                TimeController.OnLeftGordion();
            }

            _fallbackTimer = 0f;
            return;
        }

        if (!GordionLevel.IsCurrent())
        {
            if (_landedHandled)
            {
                _landedHandled = false;
                TimeController.OnLeftGordion();
            }

            _fallbackTimer = 0f;
            return;
        }

        bool landed = round.shipHasLanded && !round.shipIsLeaving;

        if (!landed && !round.shipIsLeaving && !round.inShipPhase && IsCompanySceneLoaded())
        {
            _fallbackTimer += PollInterval;
            if (_fallbackTimer >= FallbackLandedAfter && !_landedHandled)
            {
                Plugin.Log.LogWarning(
                    "shipHasLanded was never set (another mod likely altered the landing sequence) — " +
                    "starting the Gordion clock via fallback detection.");
                landed = true;
            }
        }
        else
        {
            _fallbackTimer = 0f;
        }

        if (landed && !_landedHandled)
        {
            _landedHandled = true;
            TimeController.OnLandedOnGordion();
        }
        else if (!landed && _landedHandled && (round.shipIsLeaving || !round.shipHasLanded))
        {
            _landedHandled = false;
            _fallbackTimer = 0f;
            TimeController.OnLeftGordion();
        }
    }

    private static bool IsCompanySceneLoaded()
    {
        SelectableLevel level = GordionLevel.Level;
        if (level == null || string.IsNullOrEmpty(level.sceneName))
            return false;

        Scene scene = SceneManager.GetSceneByName(level.sceneName);
        return scene.IsValid() && scene.isLoaded;
    }
}
