using System;
using UnityEngine;

namespace WeatherGordion;

/// <summary>
/// Supplies, for a running Gordion day, the three things vanilla skips because they all sit behind
/// <c>currentLevel.planetHasTime</c> — a flag this mod deliberately leaves false (see
/// <see cref="TimeController"/> for why).
///
///   * <c>normalizedTimeOfDay</c>. <c>MoveTimeOfDay</c> only updates it inside
///     <c>if (sunAnimator != null)</c>, so a moon without one would leave it pinned at 0 and take
///     progressing weather down with it. Recomputed here from <c>currentDayTime</c>, which is always
///     current, so the value is right either way.
///   * The HUD clock, which vanilla drives from the same guarded block.
///   * The midnight departure and its warning, both of which live in <c>TimeOfDayEvents</c> behind the
///     <c>planetHasTime</c> check and therefore never fire at the Company.
///
/// Runs in LateUpdate so TimeOfDay has already moved the day on for this frame.
/// </summary>
internal sealed class RealTimeDriver : MonoBehaviour
{
    private static RealTimeDriver _instance;

    private bool _departureCalled;
    private bool _warningCalled;
    private bool _pushedEndOfDay;

    public static void Begin()
    {
        if (_instance != null)
            return;

        var go = new GameObject("WeatherGordion_RealTimeDriver");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        _instance = go.AddComponent<RealTimeDriver>();
    }

    public static void Stop()
    {
        if (_instance == null)
            return;

        ClockOverlay.Hide();
        Destroy(_instance.gameObject);
        _instance = null;
    }

    private void LateUpdate()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"RealTime driver tick failed: {e.Message}");
        }
    }

    private void Tick()
    {
        var time = TimeOfDay.Instance;
        var round = StartOfRound.Instance;
        if (time == null || round == null || !TimeController.ClockRunning)
            return;

        if (!time.currentDayTimeStarted || time.totalTime <= 0f)
            return;

        KeepDayFromFreezing(time);

        float normalized = Mathf.Clamp01(time.currentDayTime / time.totalTime);

        // Harmless when the game already set it — same number from the same source.
        time.normalizedTimeOfDay = normalized;

        ClockOverlay.Draw(normalized);
        HandleEndOfDay(time, round, normalized);
    }

    /// <summary>
    /// Second line of defence for the clamp in <c>MoveGlobalTime</c>, which pins <c>globalTime</c> to
    /// <c>globalTimeAtEndOfDay</c>. <see cref="TimeController"/> makes that value land a full day ahead
    /// when the clock starts, but anything that recomputes it later — a lobby reload, another mod —
    /// could put it back behind the current time and silently stop the day again. Since a frozen
    /// globalTime also stops the rising flood water and the storm's random strikes, both of which are
    /// driven by it, it is worth re-checking rather than trusting the one-time setup.
    /// </summary>
    private void KeepDayFromFreezing(TimeOfDay time)
    {
        if (time.globalTimeAtEndOfDay > time.globalTime + 0.01f)
            return;

        // The day really has run out — that is the end-of-day state, not a stuck clock.
        if (time.currentDayTime >= time.totalTime)
            return;

        SelectableLevel level = GordionLevel.Level;
        float daySpeed = level != null ? Mathf.Max(0.01f, level.DaySpeedMultiplier) : 1f;

        // Every input here is server-synced or identical everywhere, so each machine lands on the
        // same value and nobody ends up on a different time of day.
        time.globalTimeAtEndOfDay = time.globalTime + (time.totalTime - time.currentDayTime) / daySpeed;

        if (_pushedEndOfDay)
            return;

        _pushedEndOfDay = true;
        Plugin.Log.LogWarning(
            $"Gordion's end of day had fallen behind the clock and the day would have frozen; pushed it " +
            $"to {time.globalTimeAtEndOfDay:0.##}.");
    }

    private void HandleEndOfDay(TimeOfDay time, StartOfRound round, float normalized)
    {
        if (!Plugin.Cfg.ShipLeavesAtEndOfDay.Value || round.shipIsLeaving)
            return;

        // The warning vanilla plays at 90% of the day, so the departure is not a surprise.
        if (!_warningCalled && normalized > 0.9f)
        {
            _warningCalled = true;
            try
            {
                var hud = HUDManager.Instance;
                if (hud != null)
                {
                    hud.ReadDialogue(time.shipLeavingSoonDialogue);
                    hud.shipLeavingEarlyIcon.enabled = true;
                }

                time.shipLeavingAlertCalled = true;
            }
            catch (Exception e)
            {
                Plugin.DebugLog($"Ship-leaving warning failed: {e.Message}");
            }
        }

        if (_departureCalled || !round.IsServer || normalized < time.shipLeaveAutomaticallyTime)
            return;

        _departureCalled = true;

        // Vanilla's own networked route: every client runs ShipLeaveAutomatically from this RPC, so
        // calling StartOfRound directly on the host instead would desync everyone else.
        time.SetShipToLeaveOnMidnightClientRpc();
        Plugin.Log.LogInfo("Gordion reached the end of the day — the ship is leaving on its own.");
    }
}
