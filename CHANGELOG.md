# Changelog

## 1.0.3

- **Multiplayer: every player now gets the same time of day.** The day offset was being set from the
  local `globalTime` at the moment each machine noticed the landing, so host and clients each started
  their Gordion day from a slightly different number — different clocks, different flood levels,
  different progression timing. It is a flat zero now, and `globalTime` itself is server-synced, so
  the same arithmetic runs on identical inputs everywhere.
- Lightning target rescans are host-only. `StormyWeather`'s targeting loop already returns early for
  anyone who is not the owner, so clients were paying for a scene-wide sweep that could only disagree
  with the host about what may be struck.
- The end-of-day freeze guard no longer fires when the day has genuinely run out, and reports itself
  once instead of every frame.
- README documents the networking model in full: what the host decides, what is computed locally from
  shared inputs, and what a player without the mod sees.

## 1.0.2

- **The Gordion day froze the instant it started, and took the weather with it.** TimeOfDay derives
  local time as `(globalTime + OffsetFromGlobalTime) * DaySpeedMultiplier % (totalTime + 1)` and then
  sets `globalTimeAtEndOfDay` from it; Gordion's authored offset — never meant to produce a sensible
  time on a moon with no day cycle — landed past the end of the day, so that value came out *behind*
  the current time and `MoveGlobalTime`'s clamp pinned `globalTime` in place. The log showed it stuck
  at 100 every visit. The offset is now zeroed against the current global time so the visit starts at
  dawn with a full day ahead, and the driver re-checks each frame in case anything recomputes it.
  This one bug also explains the previous report of Flooded water not rising and storms never
  striking: the water level is `globalTime / 1080 * weatherVariable2`, and random thunder fires on
  `globalTime > randomThunderTime`. Neither could ever advance.
- **Lightning now has targets on Gordion.** `StormyWeather` scans for conductive items exactly once,
  fifteen seconds after the storm starts, and the targeting loop skips anything still in the ship. On
  a moon that spawns no scrap and where every metal object starts in the ship and is then carried out
  and sold, that single scan leaves the storm with nothing it may hit. The list is now rebuilt every
  few seconds while on Gordion.
- **The HUD clock is asserted rather than requested.** `SetClockVisible` only nudges
  `HUDElement.targetAlpha`, which `HUDManager.Update` lerps towards, so a later writer or a disabled
  element wins. The canvas group's alpha is now written directly, and the element re-activated if
  something turned it off.
- Dust Clouds is no longer in the default weather line. Existing configs keep their value — remove
  `DustClouds@80` from `Weather weights` by hand, or delete the setting to pick up the new default.

## 1.0.1

- **The mod could not load at all.** The declared dependency GUID for MrovLib was `mrov.MrovLib`,
  taken from its config file name; the plugin actually registers itself as plain `MrovLib`, so
  BepInEx refused the mod with "missing dependencies" and neither the config file nor any weather
  ever appeared.
- **Flooded water stayed at world height 0 and never rose.** `RoundManager.SetToCurrentLevelWeather`
  copies `weatherVariable`/`weatherVariable2` out of the moon's own pool entry, and `FloodWeather`
  derives its rise from `globalTime / 1080 * weatherVariable2` — so the zeroes in an injected entry
  meant no depth and no rise. Injected entries now borrow their variables from a moon that defines
  the weather, which also fixes Foggy's density range collapsing to an empty span.
- **The HUD clock never showed.** Vanilla only touches clock visibility from
  `TimeOfDay.SetInsideLightingDimness`, reached from `MoveTimeOfDay` and only when `sunAnimator` is
  set, and it hides the clock whenever the player counts as indoors — which at the Company is most
  of the visit. The clock is now asserted from LateUpdate, indoors included, in both time modes.
- **New: `Ship leaves at end of day`** (RealTime, on by default). The whole midnight-departure
  branch lives in `TimeOfDayEvents` behind `planetHasTime`, so it can never fire at the Company;
  the mod now calls the game's own networked `SetShipToLeaveOnMidnightClientRpc` instead, warning
  at 90% of the day exactly like vanilla. Turn it off to keep departure manual.
- `normalizedTimeOfDay` is recomputed each frame in RealTime mode. Vanilla only updates it inside
  `if (sunAnimator != null)`, and progressing weather is driven by that value.

## 1.0.0

- First release. Weather on 71-Gordion: the moon's empty `randomWeathers` pool is filled and it is
  removed from the `Company` blacklist WeatherRegistry applies to every weather by default, so rain,
  fog, dust, storms, floods and eclipses can all happen at the Company.
- Per-weather weights in one config line, using the same names as the section titles in
  `mrov.WeatherRegistry.cfg` — so combined and progressing weathers from WeatherTweaks and Combined
  Weathers Toolkit work by name too.
- Three time modes. `RealTime` starts the game's own clock for the visit so progressing weather and
  the rising Flooded water work; `Simulated` drives a day and the HUD clock entirely on its own timer
  without touching the game clock; `Off` keeps the moon timeless.
- `planetHasTime` is deliberately never changed. It is not what runs the clock — `TimeOfDay.Update`
  keys off `currentDayTimeStarted` — but it *is* what decides whether leaving burns a deadline day,
  whether the ship departs at midnight, whether landing late is refused, and whether the end-of-round
  stats screen plays. Leaving it alone keeps all of that vanilla, and keeps every other mod that
  identifies the Company moon by `!planetHasTime` working.
- The quota deadline is held at its pre-landing value while the clock runs, because
  `TimeOfDay.MoveGlobalTime` drains it regardless of moon and would otherwise cost days and move the
  company buying rate.
- Nothing is written to `mrov.WeatherRegistry.cfg`: filters and weights are changed in memory with
  BepInEx config saving suppressed for the duration of the write.
