# HP2 ShiftLight

An external tachometer and gear advisor for the PS2 recompilation of Need for Speed: Hot Pursuit 2 on Windows x64. Supports the stock 0.14 runtime and the Timelapse Clouds runtime. The overlay shows RPM, current gear, recommended gear and the estimated RPM after the shift.

[Download for Windows](https://github.com/SamMontreal/HP2-ShiftLight/releases/latest) · [中文使用说明](README.zh-CN.txt)

The 1.5.0 driving panel adds road speed in km/h and a two-axis G meter beside engine RPM and gear advice. The marker moves right under rightward acceleration and up under forward acceleration. Braking moves it down. Each ring is 1 g. The signed numbers show horizontal vehicle acceleration even beyond the outer ring. The gauge projects the game’s world acceleration onto the current vehicle heading and smooths it over 90 ms of simulation time.

## Install and play

1. Extract the Windows ZIP alongside the game's `speed.exe`.
2. Run `Play-HP2-ShiftLight.cmd` to start both the overlay and the game. If `Play-Volumetric-Clouds.cmd` is installed, this launcher uses it. To attach to a running game, launch `HP2-ShiftLight.exe` directly.
3. The translucent overlay centers itself at the bottom of the game monitor. Red means shift up. Green means shift down. Follow the displayed gear number. When the suggested gear is several gears away, use successive inputs in sequential manual mode.

## How it chooses a gear

The advisor reads the local player's nine-point engine torque curve, forward gear ratios, throttle and clutch state through a read-only Windows handle. It compares full-throttle wheel force at the same road speed in every usable gear. Estimates assume an engaged clutch. Wheelspin or a change of speed during a shift can affect the actual landing RPM.

Red requests the next sequential gear once it provides more wheel force at both the current and estimated engagement speed. Amber can prepare the driver before crossover. A safe lower gear can trigger green on the first fresh sample at 15% throttle and 1% force gain (0.4% to hold). The estimate uses recent road acceleration and separately observed shift durations and speed changes for completed upshifts and downshifts. Before a sample is available it uses a 220 ms estimate. Lower gears are screened against the vehicle's redline with extra headroom. When the game supplies no valid redline, the fallback is 90% of maximum RPM. The panel clears its lights during gear changes, pauses and replays.

## Build

Run `build.cmd` on 64-bit Windows with the .NET Framework 4.x C# compiler installed. The resulting `HP2-ShiftLight.exe` belongs next to the game's `speed.exe`. The Windows release includes the source and launcher. The game is distributed separately.

