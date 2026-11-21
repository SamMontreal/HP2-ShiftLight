# HP2 ShiftLight

An external tachometer and gear advisor for the PS2 recompilation of Need for Speed: Hot Pursuit 2 on Windows x64. Supports the stock 0.14 runtime and the Timelapse Clouds runtime. The overlay shows RPM, current gear, recommended gear and the estimated RPM after the shift.

[Download for Windows](https://github.com/SamMontreal/HP2-ShiftLight/releases/latest) · [中文使用说明](README.zh-CN.txt)

## Install and play

1. Extract the Windows ZIP alongside the game's `speed.exe`.
2. Run `Play-HP2-ShiftLight.cmd` to start both the overlay and the game. If `Play-Volumetric-Clouds.cmd` is installed, this launcher uses it. To attach to a running game, launch `HP2-ShiftLight.exe` directly.
3. The translucent overlay centers itself at the bottom of the game monitor. Red means shift up. Green means shift down. Follow the displayed gear number. When the suggested gear is several gears away, use successive inputs in sequential manual mode.

## How it chooses a gear

The advisor reads the local player's nine-point engine torque curve, forward gear ratios, throttle and clutch state through a read-only Windows handle. It compares full-throttle wheel force at the same road speed in every usable gear. Estimates assume an engaged clutch. Wheelspin or a change of speed during a shift can affect the actual landing RPM.

The advisor confirms downshifts after 60 ms when the lower gear offers at least 4% more force with at least 25% throttle. At 35% throttle or more, steady RPM rise can show an amber preparation cue up to 120 ms and 180 RPM before the target. The panel flashes red once the upshift target is reached and the higher gear provides at least the current wheel force. Where the lower gear stays stronger throughout its rev range, the target is fuel cut. The percentage is the estimated full-throttle power after shifting now, relative to the engine’s peak. The lights clear during shifts, pauses and replays.

## Build

Run `build.cmd` on 64-bit Windows with the .NET Framework 4.x C# compiler installed. The resulting `HP2-ShiftLight.exe` belongs next to the game's `speed.exe`. The Windows release includes the source and launcher. The game is distributed separately.

