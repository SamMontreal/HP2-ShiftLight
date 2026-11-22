# HP2 ShiftLight

An external tachometer and gear advisor for the PS2 recompilation of Need for Speed: Hot Pursuit 2 on Windows x64. Supports the stock 0.14 runtime and the Timelapse Clouds runtime. The overlay shows road speed, RPM, ten shift LEDs, current and suggested gears, and lateral and longitudinal G.

[Download for Windows](https://raw.githubusercontent.com/SamMontreal/HP2-ShiftLight/windows-v1.7.1/HP2-ShiftLight-v1.7.1-Windows-x64.zip) · [中文使用说明](README.zh-CN.txt)

The main panel is centred at the bottom of the game window. The HP panel appears beside it in police-enabled races when driving as a racer. Both pass clicks through to the game and leave keyboard focus with the player. The main gauge opens at launch, displays a waiting status in the menus and switches to live readings when you drive. Both close when their game exits.

## Install and play

1. Extract the Windows ZIP alongside the game's `speed.exe`.
2. Run `Play-HP2-ShiftLight.cmd` to launch the game and the dashboard together. If `Play-Volumetric-Clouds.cmd` is installed, this launcher uses it. To attach to a running game, launch `HP2-ShiftLight.exe` directly.
3. The gear panel turns red with an up arrow for an upshift and green with a down arrow for a downshift. The large white number is the suggested gear. When you change gear, the current gear number briefly appears on a white background. In sequential manual mode, use successive inputs to reach a suggestion several gears away.

## How it chooses a gear

The advisor reads the local player's nine-point torque curve, gear ratios, throttle and clutch state. At the same road speed, it compares engine torque multiplied by gear ratio in each adjacent gear. The high-RPM crossover where the next gear takes over sets the shift point. If the current gear still pulls harder, the target is 75 RPM below the game's fuel cut. This calculation assumes an engaged clutch and grip at the driven wheels. [High Performance Academy, Gear Changes](https://www.hpacademy.com/courses/professional-motorsport-data-analysis/acceleration-and-gearing-gear-changes)

The first nine LEDs accumulate in green, amber and red from 82% to 98% of the gear's target. The whole strip flashes red for the upshift. With steadily rising RPM under load, the cue can lead the target by up to 120 ms, capped at 180 RPM. Lifting off clears the upshift lights. A downshift cue can still appear when a lower gear safely improves available wheel force both now and at estimated clutch engagement. The strip clears during shifts, in top gear and outside driving. The ten-LED, gear-dependent display follows AiM's programmable arrangement. The thresholds above are the settings chosen for HP2. [AiM Shift Light Module](https://www.aimsports.com/us/products/shift-light-module/index.htm)

Landing power is the estimated full-throttle power after clutch engagement as a percentage of the car's peak power. It uses the recommended lower gear on a downshift and the next higher gear otherwise. A dash indicates that no landing estimate is available.

The G-meter averages lateral and longitudinal inertial load over the last 0.5 seconds of simulation time, weighted by elapsed time. The rings mark 1, 2 and 3 g. Beyond 3 g the marker stays at the rim and the numbers retain the measured averages.

## Hot Pursuit air warning

Timelapse Clouds 1.4.11 starts the installed dashboard when the game opens. With older cloud runtimes or the original sky, use the joint launcher to start both at launch. With the stock game, use the included joint launcher. The air panel tracks the local racer's helicopter target. Before selection it can list eligible attacks. After selection it reads the game's committed preset and reports rockets, barrel drops (including paired drops) or spike strips ahead of the first salvo. It clears a cancelled run or a change of target. It stays hidden in ordinary races and when driving as the officer.

## Build

Run `build.cmd` on 64-bit Windows with the .NET Framework 4.x C# compiler installed. The resulting `HP2-ShiftLight.exe` belongs next to the game's `speed.exe`. The Windows release includes the source and launcher. The game is distributed separately.

