# HP2 ShiftLight

An external tachometer and gear advisor for the PS2 recompilation of Need for Speed: Hot Pursuit 2 on Windows x64. Supports the stock 0.14 runtime and the Timelapse Clouds runtime. The overlay shows RPM, current gear, recommended gear and the estimated RPM after the shift.

[Download for Windows](https://raw.githubusercontent.com/SamMontreal/HP2-ShiftLight/windows-v1.6.0/HP2-ShiftLight-v1.6.0-Windows-x64.zip) · [中文使用说明](README.zh-CN.txt)

The 1.6.0 main panel is centred at the bottom of the game window. The HP panel appears beside it in police-enabled races when driving as a racer. Both use translucent charcoal, silver framing and amber highlights drawn from the original race HUD. Both close when their game exits. The speed and RPM displays are live. The G marker and readings average the driver's lateral and longitudinal inertial load over the last 0.5 seconds of simulation time, weighted by elapsed time.

## Install and play

1. Extract the Windows ZIP alongside the game's `speed.exe`.
2. Run `Play-HP2-ShiftLight.cmd` to start both the overlay and the game. If `Play-Volumetric-Clouds.cmd` is installed, this launcher uses it. To attach to a running game, launch `HP2-ShiftLight.exe` directly.
3. The translucent overlay centers itself at the bottom of the game monitor. Red means shift up. Green means shift down. Follow the displayed gear number. When the suggested gear is several gears away, use successive inputs in sequential manual mode.

## How it chooses a gear

The advisor reads the local player's nine-point engine torque curve, forward gear ratios, throttle and clutch state through a read-only Windows handle. It compares full-throttle wheel force at the same road speed in every usable gear. Estimates assume an engaged clutch. Wheelspin or a change of speed during a shift can affect the actual landing RPM.

Red indicates the next sequential gear will land at peak available engine power with full throttle. When the peak cannot be reached before fuel cut, the advisor maximises attainable landing power while leaving 75 RPM of headroom. The percentage shows predicted landing power relative to the actual engine peak. Amber can lead the cue by up to 120 ms. Lifting off or braking still permits green downshift advice when the lower gear safely improves available wheel force on both sides of clutch engagement. The clutch-open estimate starts at 220 ms and learns separately from completed shifts. Advice clears during shifts, pauses and replays.

## Hot Pursuit air warning

The optional 1.4.6 cloud runtime launches the installed companion when a police-enabled race starts. With the stock game, use the included joint launcher. The air panel tracks the local racer's helicopter target. Before selection it can list eligible attacks. After selection it reads the game's committed preset and reports rockets, barrel drops (including paired drops) or spike strips ahead of the first salvo. It clears a cancelled run or a change of target. It stays hidden in ordinary races and when driving as the officer.

## Build

Run `build.cmd` on 64-bit Windows with the .NET Framework 4.x C# compiler installed. The resulting `HP2-ShiftLight.exe` belongs next to the game's `speed.exe`. The Windows release includes the source and launcher. The game is distributed separately.

