# How Headroom works

**In short:** the AMD driver reads a power table from the registry when it starts. Headroom edits four numbers
in a copy of that table, writes it to the registry, and restarts the GPU so the driver loads it. Nothing is
patched, and the vBIOS is never touched.

## The registry value

```
HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000X
    PP_PhmSoftPowerPlayTable
```

If this value exists, the driver uses it instead of the table in the vBIOS. Headroom writes it, then runs
`pnputil /restart-device`. Applying Factory deletes the value.

## The table format

Headroom supports only the SMU 11 table used by RX 5000 ("Navi 1x"), format revision 12.0. The layout is
public in the Linux kernel: `smu_v11_0_pptable.h` covers the outer table, and `smu11_driver_if_navi10.h`
covers `PPTable_t`, which starts at byte 482.

`PowerTable.Check` rejects a table unless:

- its size field matches its length, and it is at least 1024 bytes;
- the format is 12.0 and the driver part is 482 bytes;
- the key fields hold plausible values (for example, a max clock between 500 and 2500 MHz).

Only these fields are written:

| Setting | Field | Offset | Unit |
| --- | --- | --- | --- |
| Top speed | `FreqTableGfx[1]` | 816 | MHz |
| Power limit | `SocketPowerLimitAc[0]` | 494 | W |
| Power limit on battery | `SocketPowerLimitDc[0]` | 510 | W (only ever lowered) |
| Voltage ceiling | `MaxVoltageGfx` | 590 | mV × 4 |
| Hotspot limit | `ThotspotLimit` | 536 | °C |

Two more fields are read for validation: `FreqTableGfx[0]` (814) and `MinVoltageGfx` (586).

## How the fields were verified

Each field was A/B tested on an RX 5500M. The test changed one value, restarted the GPU, ran the load test,
compared the result with the unchanged table, and then restored it.

| Change | Result |
| --- | --- |
| Top speed 1500 → 1200 MHz | clock 1440 → 1157 MHz |
| Top speed 1500 → 1580 MHz | clock 1440 → 1522 MHz, voltage hit the ceiling |
| Power 70 → 45 W | power capped at 44–45 W, clock ~1295 MHz |
| Voltage 962.5 → 900 mV | voltage capped at 900 mV, clock 1440 → 1412 MHz, power 55 → 51 W |
| Hotspot 100 → 80 °C | hotspot held at 78–81 °C by throttling |
| Table removed | same as the unchanged table |

Notes from these runs:

- The reported clock is always about 4% below the table value.
- The voltage setting is a ceiling, not an offset. Lowering it also costs a little speed.
- The table's fan fields were not used, because laptop fans are controlled elsewhere.

## Reference tables and limits

Headroom never ships a table. Each PC stores its own **reference table** in `tables\`, named after the device
and subsystem ID. It comes from an imported vBIOS (`*.vbios.bin`), or else from the registry on first run
(`*.registry.bin`). Every apply copies the reference and changes only the four settings.

Slider ranges come from the reference (`Limits` in `src/Core.cs`). *Default* is what the sliders use out of
the box. *Hard* is the widest allowed, and `PowerTable.Apply` enforces it. Ranges set in Settings are always
clamped inside Hard.

| Setting | Default | Hard |
| --- | --- | --- |
| Top speed | 50% of ref → ref + 8% | table min clock + 100 MHz → ref + 20% |
| Power | 35% of ref → ref | 20% of ref (min 10 W) → ref |
| Voltage | ref − 160 mV (≥ table min + 50 mV) → ref | table min + 25 mV → ref |
| Hotspot | 70 °C → ref | 60 °C → ref |

## The load test

`src/LoadTest.cs` runs an OpenCL compute kernel in batches of about 0.1 s and reads AMD's sensors (ADL) twice
a second. Averages skip the first quarter of the run.

Every eighth batch is read back and compared with the first. The work is identical, so the output must be
too. A mismatch, an OpenCL error or a hang counts as a failed test.

The test runs in a helper process (`Headroom.exe --loadtest N "GPU name"`). This keeps stale handles out of
the main window after a GPU restart, and it sidesteps a stack overflow in AMD's OpenCL runtime when it
unloads at exit.

## Calibration

`Calibration.Run` (`src/Core.cs`) is pure logic. It receives a "measure these settings" function, which lets
`src/Tests.cs` drive it with a simulated GPU. `src/Calibrator.cs` provides the real function: write the
table, restart the GPU, wait 3 s, and run the load test.

Rules:

- Factory is tested first. If it fails, calibration stops.
- Only one setting changes at a time. Power and hotspot stay at their reference values.
- Every value tried stays inside the current ranges.
- **Voltage** steps down 24 mV at a time. It stops at the first failure, or when speed drops more than 3%.
- **Speed** steps up 2% at a time. It stops at a failure, when the hotspot comes within 2 °C of its limit,
  or when a step gains less than a quarter of a step in measured speed. If a failure was seen, the result is
  one step below the highest pass.
- If the user stops a test, the run is cancelled, not counted as a failure, and nothing is saved.

Results are saved to `tables\<device>.calibration` and only feed the Efficient and Boost presets.

**Crash safety:** before the first step, Headroom copies the current table to `tables\<device>.interrupted`
and registers a `RunOnce` entry for `Headroom.exe --recover`. Both are removed when the run ends. If either
is still there later, the table is restored. A named mutex stops a second instance from recovering while a
run is in progress.

## Code map

| File | Contents |
| --- | --- |
| `src/Core.cs` | Pure logic: table checks and edits, limits, presets, profiles, vBIOS reader |
| `src/Tests.cs` | Test runner for `Core.cs`, using synthetic tables only |
| `src/Gpu.cs` | GPU discovery, registry, reference store, backups, device restart |
| `src/Sensors.cs` | Live readings through ADL |
| `src/LoadTest.cs` | OpenCL load and helper process |
| `src/Calibrator.cs` | Runs calibration on the real GPU |
| `src/Monitor.cs` | Graph control |
| `src/Settings.cs` | Settings dialog |
| `src/Controls.cs` | Theme and custom controls |
| `src/Results.cs` | Test results panel and status line |
| `src/MainForm.cs` | Main window |
| `src/Program.cs` | Entry point and command-line modes |

## Building

```
build.cmd          run the tests, then build Headroom.exe
build.cmd tests    run the tests only
```

It uses the .NET Framework 4.x C# compiler that ships with Windows.

## Adding another table format

RX 6000 uses a different table (SMU 11.0.7 / 13). To add it:

1. Add its header check and field offsets next to `PowerTable`, taken from the matching Linux header.
2. Add tests with a synthetic table in that layout.
3. A/B test every field on real hardware and record the numbers.

Don't add a format from the headers alone. Only a measurement shows whether the Windows driver actually obeys
a field.
