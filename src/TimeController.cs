using System;

namespace WeatherGordion;

/// <summary>
/// Makes the day pass on Gordion in <see cref="GordionTimeMode.RealTime"/>.
///
/// The obvious lever — <c>planetHasTime</c> — is the wrong one. It is not what drives the clock:
/// <c>TimeOfDay.Update</c> only ever checks <c>currentDayTimeStarted</c>, and StartOfRound's landing
/// coroutine consults <c>planetHasTime</c> exactly once, to decide whether to set that flag. What
/// <c>planetHasTime</c> *does* control everywhere else is the moon's identity: whether leaving burns a
/// deadline day (<c>PassTimeToNextDay</c>), whether the ship flies off at midnight and warns you first
/// (<c>TimeOfDayEvents</c>), whether landing is refused as "too late on moon", whether the end-of-round
/// stats screen plays, and whether the moon can be drawn for a challenge file.
///
/// So this class starts the clock directly and leaves <c>planetHasTime</c> false. Gordion keeps behaving
/// like the Company building in every one of those checks — and, as a side effect of the same flag,
/// every other mod that identifies the Company moon by <c>!planetHasTime</c> keeps working.
///
/// One thing does leak through: <c>TimeOfDay.MoveGlobalTime</c> subtracts elapsed time from
/// <c>timeUntilDeadline</c> unconditionally. <see cref="Patches.TimeOfDayPatches"/> puts that back.
/// </summary>
internal static class TimeController
{
    private static bool _clockStarted;
    private static float _deadlineSnapshot;
    private static bool _hasDeadlineSnapshot;

    private static SelectableLevel _speedPatchedLevel;
    private static float _originalDaySpeed;

    private static SelectableLevel _offsetPatchedLevel;
    private static float _originalOffset;

    /// <summary>True while this mod is running the vanilla clock on Gordion.</summary>
    public static bool ClockRunning => _clockStarted;

    /// <summary>The deadline value to hold, valid only while <see cref="ClockRunning"/>.</summary>
    public static bool TryGetFrozenDeadline(out float value)
    {
        value = _deadlineSnapshot;
        return _clockStarted && _hasDeadlineSnapshot && Plugin.Cfg.FreezeDeadline.Value;
    }

    /// <summary>Called once the ship has actually landed on Gordion.</summary>
    public static void OnLandedOnGordion()
    {
        if (Plugin.Cfg == null || !Plugin.Cfg.Enabled.Value)
            return;

        switch (Plugin.Cfg.TimeMode.Value)
        {
            case GordionTimeMode.RealTime:
                StartVanillaClock();
                StormyMetal.Begin();
                break;

            case GordionTimeMode.Simulated:
                SimulatedClock.Begin();
                StormyMetal.Begin();
                break;

            case GordionTimeMode.Off:
            default:
                break;
        }
    }

    /// <summary>Called when the ship leaves Gordion, or the round tears down for any other reason.</summary>
    public static void OnLeftGordion()
    {
        SimulatedClock.Stop();
        RestoreImmediately();
    }

    /// <summary>Undoes everything this class changed. Safe to call at any time, including twice.</summary>
    public static void RestoreImmediately()
    {
        RealTimeDriver.Stop();
        StormyMetal.Stop();
        RestoreDeadline();
        RestoreDaySpeed();

        // currentDayTimeStarted / movingGlobalTimeForward are cleared by StartOfRound's own ship-leave
        // coroutine, but a disconnect can skip it, so clear them here too.
        if (_clockStarted)
        {
            var time = TimeOfDay.Instance;
            if (time != null)
            {
                time.currentDayTimeStarted = false;
                time.movingGlobalTimeForward = false;
            }

            _clockStarted = false;
            Plugin.DebugLog("Gordion clock stopped.");
        }

        _hasDeadlineSnapshot = false;
    }

    private static void StartVanillaClock()
    {
        var time = TimeOfDay.Instance;
        if (time == null)
        {
            Plugin.Log.LogWarning("Landed on Gordion but TimeOfDay is missing — the clock stays stopped.");
            return;
        }

        if (_clockStarted || time.currentDayTimeStarted)
        {
            // Something already started the day; don't fight it, but still hold the deadline and make
            // sure the pieces vanilla skips are being supplied.
            SnapshotDeadline(time);
            _clockStarted = true;
            RealTimeDriver.Begin();
            return;
        }

        EnsureDaySpeed();
        ZeroDayOffset(time);
        SnapshotDeadline(time);

        // The pair StartOfRound sets for every other moon. TimeOfDay.Update picks it up on the next
        // frame, runs its own one-shot initialisation and starts moving the day forward from there.
        time.currentDayTimeStarted = true;
        time.movingGlobalTimeForward = true;
        _clockStarted = true;

        // Supplies the pieces vanilla skips on a moon without planetHasTime: the HUD clock, a reliable
        // normalizedTimeOfDay, and the end-of-day departure.
        RealTimeDriver.Begin();

        SelectableLevel level = GordionLevel.Level;
        Plugin.Log.LogInfo(
            $"Gordion clock started (globalTime {time.globalTime:0.##}, totalTime {time.totalTime:0.##}, " +
            $"daySpeed {level?.DaySpeedMultiplier:0.###}, offset {level?.OffsetFromGlobalTime:0.##}, " +
            $"deadline held: {Plugin.Cfg.FreezeDeadline.Value}).");
    }

    /// <summary>
    /// Gives the visit a day that can actually run.
    ///
    /// TimeOfDay derives the moon's local time as
    /// <c>(globalTime + OffsetFromGlobalTime) * DaySpeedMultiplier % (totalTime + 1)</c>, and then sets
    /// <c>globalTimeAtEndOfDay = globalTime + (totalTime - currentDayTime) / DaySpeedMultiplier</c>.
    /// Gordion's authored offset was never meant to produce a sensible local time — the moon has no day
    /// cycle — and it lands past the end of the day, which makes that end-of-day value smaller than the
    /// current time. <c>MoveGlobalTime</c> clamps to it, so the clock froze the instant it started.
    ///
    /// The offset is zeroed rather than set relative to the current clock. That matters for multiplayer:
    /// an offset derived from <c>globalTime</c> would be captured at whatever moment each machine
    /// happened to detect the landing, so every player would end up on a slightly different time of day.
    /// Zero is the same number everywhere, and <c>globalTime</c> itself is server-synced, so host and
    /// clients compute an identical local time from identical inputs.
    /// </summary>
    private static void ZeroDayOffset(TimeOfDay time)
    {
        SelectableLevel level = GordionLevel.Level;
        if (level == null)
            return;

        _offsetPatchedLevel = level;
        _originalOffset = level.OffsetFromGlobalTime;
        level.OffsetFromGlobalTime = 0f;

        Plugin.DebugLog(
            $"Gordion OffsetFromGlobalTime {_originalOffset:0.##} -> 0 so the day can run " +
            $"(globalTime {time.globalTime:0.##} is server-synced, so every player gets the same time).");
    }

    /// <summary>
    /// The day cannot advance with a zero speed multiplier, and TimeOfDay's start-of-day maths divides
    /// by it. Gordion ships with the default of 1, but a moon loader could have zeroed it precisely
    /// because the moon was never meant to have time.
    /// </summary>
    private static void EnsureDaySpeed()
    {
        SelectableLevel level = GordionLevel.Level;
        if (level == null)
            return;

        Plugin.DebugLog($"Gordion DaySpeedMultiplier = {level.DaySpeedMultiplier}, " +
                        $"OffsetFromGlobalTime = {level.OffsetFromGlobalTime}.");

        if (level.DaySpeedMultiplier > 0f)
            return;

        _speedPatchedLevel = level;
        _originalDaySpeed = level.DaySpeedMultiplier;
        level.DaySpeedMultiplier = 1f;

        Plugin.Log.LogInfo(
            $"Gordion had DaySpeedMultiplier = {_originalDaySpeed}, which would freeze the day and " +
            "divide by zero at the start of it; using 1 for this visit.");
    }

    private static void RestoreDaySpeed()
    {
        if (_speedPatchedLevel != null)
        {
            _speedPatchedLevel.DaySpeedMultiplier = _originalDaySpeed;
            _speedPatchedLevel = null;
        }

        if (_offsetPatchedLevel != null)
        {
            _offsetPatchedLevel.OffsetFromGlobalTime = _originalOffset;
            _offsetPatchedLevel = null;
        }
    }

    private static void SnapshotDeadline(TimeOfDay time)
    {
        if (_hasDeadlineSnapshot)
            return;

        _deadlineSnapshot = time.timeUntilDeadline;
        _hasDeadlineSnapshot = true;
        Plugin.DebugLog($"Deadline snapshot: timeUntilDeadline = {_deadlineSnapshot:0.##}.");
    }

    private static void RestoreDeadline()
    {
        if (!_hasDeadlineSnapshot || !_clockStarted)
            return;

        var time = TimeOfDay.Instance;
        if (time == null || !Plugin.Cfg.FreezeDeadline.Value)
            return;

        try
        {
            time.timeUntilDeadline = _deadlineSnapshot;
            time.UpdateProfitQuotaCurrentTime();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not restore the deadline after a Gordion visit: {e.Message}");
        }
    }
}
