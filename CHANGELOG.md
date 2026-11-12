# Changelog

## 0.2.0 - 2026-11-12

- Ranges: Settings decide where each slider starts and ends (top speed up to 20% above factory; power,
  voltage and hotspot never above it). Calibration searches inside them and now sets presets only, so the
  sliders no longer move between runs.
- Monitor: graphs of speed, voltage, power, hotspot and GPU temperature with the applied limit drawn in.
- The fourth ceiling is named "Hotspot limit", and tests report hotspot peak and GPU temperature peak separately.
- Settings dialog: ranges, which graphs to show, light/dark theme and six accent colours.
- Calibration (window and `--calibrate`): benchmarks the GPU step by step and sets the Efficient and
  Boost presets from the measurements, and records why each search stopped. Previous settings are restored
  afterwards, on stop, and after an interruption.
- Ghost markers: until changes are applied, a dashed ghost stays on each applied value and one line lists
  every pending change.
- Test length can be chosen (20 seconds to 10 minutes); a running test shows progress and can be stopped.
  The result says which settings were tested and for how long.
- Status line shows the kind of message with a coloured dot; calibration state is linked from the header;
  mouse wheel, double-click and Esc work on the rails; tooltips on every control.
- Works from each GPU's own reference table instead of a built-in one. Limits and presets scale to that table.
- Accepts any Radeon whose table is in the RX 5000-series format; other formats are refused. Models other
  than the tested RX 5500M are labelled untested and ask before the first apply.
- vBIOS import (window and `--import`).
- New look: light and dark themes that follow Windows, rounded controls, scale marks on every rail.
- Test results shown as tiles, each with its change since the previous test.
- Renamed from "RX 5500M Tuner" to Headroom.

## 0.1.0 - 2026-11-11

- First version, for one RX 5500M laptop: four ceilings, presets, one-click apply with GPU restart,
  20-second load test with a calculation check, profiles, `--apply` and `--reset`.
