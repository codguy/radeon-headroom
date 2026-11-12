# How Headroom works

## The mechanism

When the AMD graphics driver starts, it looks for a registry value named `PP_PhmSoftPowerPlayTable` under the
GPU's display-class key:

```
HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000X
```

If the value exists, the driver uses it in place of the "PowerPlay" table stored in the GPU's vBIOS. Headroom
writes that value and then restarts the GPU device (`pnputil /restart-device`) so the driver starts again and
reads it. Applying the reference's own settings deletes the value, which puts the GPU back on its vBIOS table.

Nothing in the driver is patched or bypassed. The vBIOS is never modified.

## The table format

Headroom supports one layout: the SMU 11 table used by the RX 5000 "Navi 1x" family, format revision 12.0.
The layout is public in the Linux kernel (`drivers/gpu/drm/amd/pm`): `smu_v11_0_pptable.h` describes the outer
table and `smu11_driver_if_navi10.h` describes the firmware part (`PPTable_t`) that starts at byte 482.

A table is accepted only if all of this holds (`PowerTable.Check`):

- its size field equals its length, and it is at least 1024 bytes;
- format revision is 12.0 and the driver part is 482 bytes;
- the fields below hold plausible values (for example a maximum clock between 500 and 2500 MHz).

Only these fields are ever written:

| Setting | Field in `PPTable_t` | Byte offset | Stored as |
| --- | --- | --- | --- |
| Top speed | `FreqTableGfx[1]` | 816 | MHz |
| Power limit | `SocketPowerLimitAc[0]` | 494 | W |
| Power limit on battery | `SocketPowerLimitDc[0]` | 510 | W, only ever lowered |
| Voltage ceiling | `MaxVoltageGfx` | 590 | mV × 4 |
| Hotspot limit | `ThotspotLimit` | 536 | °C |

Two more are read for validation and limits: `FreqTableGfx[0]` (814) and `MinVoltageGfx` (586).

`ThotspotLimit` acts on the hotspot sensor (the hottest point of the chip), not on the overall GPU temperature.
The window names it accordingly and plots both.

## How each field was verified

Each field was confirmed by an A/B test on an RX 5500M: change one value, restart the GPU, run the load test,
compare with the same test on the unchanged table, then restore.

| Change | Result |
| --- | --- |
| Top speed 1500 → 1200 MHz | reported clock 1440 → 1157 MHz |
| Top speed 1500 → 1580 MHz | reported clock 1440 → 1522 MHz, voltage rose to the ceiling |
| Power limit 70 → 45 W | power capped at 44–45 W, clock dropped to ~1295 MHz |
| Voltage ceiling 962.5 → 900 mV | voltage capped at 900 mV, clock 1440 → 1412 MHz, power 55 → 51 W |
| Hotspot limit 100 → 80 °C | hotspot held at 78–81 °C by throttling |
| Table removed | identical to the unchanged table |

Things worth knowing from those runs:

- The clock the driver reports sits about 4% below the number in the table, at every setting.
- The voltage ceiling is a ceiling, not an offset. The GPU follows its own voltage/frequency curve, so
  lowering the ceiling also costs a little speed.
- Fan fields in the table were not used. Laptop fans are controlled elsewhere.

## Reference tables and limits

Headroom never ships a table. Each GPU's *reference table* is stored on that PC in `tables\`, named after the
device and subsystem ID. It comes from an imported vBIOS (`*.vbios.bin`) or, failing that, from the table found
in the registry on first run (`*.registry.bin`). Every apply starts from the reference and writes the four
settings into a copy, so the output is always "reference plus four fields".

Ranges are derived from the reference (`Limits` in `src/Core.cs`), in three layers. *Hard* is the widest a
setting may ever go and is what `PowerTable.Apply` enforces. *Default* is what the sliders use out of the box.
A range chosen in Settings replaces the default and is always pulled inside Hard:

| Setting | Default range | Hard range |
| --- | --- | --- |
| Top speed | 50% of reference to reference + 8% | table minimum clock + 100 MHz to reference + 20% |
| Power limit | 35% of reference to reference | 20% of reference (at least 10 W) to reference |
| Voltage ceiling | reference − 160 mV (at least 50 mV above the table's minimum voltage) to reference | table minimum voltage + 25 mV to reference |
| Hotspot limit | 70 °C to reference | 60 °C to reference |

If the reference came from the registry and had been edited before, those edits define the limits. Importing
the vBIOS fixes that.

## The load test

`src/LoadTest.cs` runs a compute kernel on the chosen GPU through OpenCL in batches of about 0.1 second, and
samples AMD's sensor log (ADL) twice a second. Averages skip the first quarter of the run.

Identical work must produce identical output, so every eighth batch is read back and compared with the first.
A mismatch, an OpenCL error or a hang is reported as a failed test, which is how an unstable overclock or
voltage ceiling shows up.

The test runs in a helper process (`Headroom.exe --loadtest N "GPU name"`), for two reasons: a GPU restart
leaves no stale handles in the window's process, and AMD's OpenCL runtime can overflow the stack while
unloading at exit. The helper suppresses error dialogs and ends itself without unloading libraries.

## Calibration

The procedure lives in `Calibration.Run` (`src/Core.cs`) and knows nothing about hardware. It is handed a
"measure these settings" function and decides what to try next, which is why it can be tested against a
simulated GPU in `src/Tests.cs`. `src/Calibrator.cs` supplies the real function: write the table, restart the
GPU, wait three seconds, run the 20-second load test.

Rules it follows:

- Factory is measured first. If factory fails, nothing else is tried.
- One setting changes at a time; power and hotspot limits stay at their reference values throughout.
- Every setting tried is inside the ranges chosen in Settings (or the defaults).
- Voltage: steps of 24 mV downwards. Stop at the first failed test, or when average speed falls more than 3%
  below the baseline (that step still counts as "passed", just not as "efficient").
- Speed: steps of 2% upwards. Stop at the first failed test, when peak hotspot comes within 2 °C of the
  hotspot limit, or when a step gains less than a quarter of a step in measured speed.
- The recommended speed is one step below the highest pass if a failure was seen, otherwise the highest pass.

A test that the user stops is not a failed step: `Calibrator` turns it into a cancellation, so the run ends
with nothing saved and can never record a stopped test as "unstable".

The result, including the reason each search ended, is stored as `tables\<device>.calibration`. It only feeds the presets:
Efficient takes the measured voltage ceiling and Boost the measured top speed, both clamped into the current
ranges. The sliders keep their ranges, so they do not move from one calibration to the next.

Crash safety: before the first step the current registry table is copied to `tables\<device>.interrupted`
and a `RunOnce` entry is registered to run `Headroom.exe --recover` at the next sign-in. Both are removed when
the run ends. If either is found later, the saved table is written back and the GPU restarted. A named mutex
stops a second Headroom process from "recovering" while a run is still in progress.

## Code map

| File | What it holds |
| --- | --- |
| `src/Core.cs` | All pure logic: table checks and edits, limits, presets, profiles, vBIOS reader, result handling. |
| `src/Tests.cs` | Console test runner for `Core.cs`. Synthetic tables only. |
| `src/Gpu.cs` | Finding the GPU, registry access, reference store, backups, device restart. |
| `src/Sensors.cs` | Live readings through ADL. |
| `src/LoadTest.cs` | The OpenCL load and its helper process. |
| `src/Monitor.cs` | The graph control. |
| `src/Settings.cs` | The settings dialog (ranges, monitor, appearance). |
| `src/Calibrator.cs` | Runs the calibration procedure against the real GPU and stores the result. |
| `src/Controls.cs` | Theme and custom controls: rail (stop, ghost, live marker), buttons, card, value label. |
| `src/Results.cs` | The measuring panel (hint, progress, result tiles) and the status line. |
| `src/MainForm.cs` | The window: state, apply, test, calibration, profiles, vBIOS import. |
| `src/Program.cs` | Entry point and command-line modes. |

## Adding another table format

RX 6000 cards use a different table (SMU 11.0.7 / 13). Supporting one means:

1. Add its header check and field offsets next to `PowerTable`, taken from the matching Linux header.
2. Add tests with a synthetic table in that layout.
3. Verify every field with the A/B method above on real hardware, and record the numbers.

Please do not add a format on the strength of the headers alone. A field that the Windows driver ignores, or
reads differently, only shows up in a measurement.
