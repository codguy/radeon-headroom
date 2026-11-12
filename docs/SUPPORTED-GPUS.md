# Supported GPUs

Headroom decides by **table format**, not by GPU name. It reads the power table, and if the table is not the
RX 5000-series layout (format 12.0) it refuses to do anything.

## Tested

| GPU | Machine | Driver | Result |
| --- | --- | --- | --- |
| Radeon RX 5500M (`DEV_7340`, `SUBSYS_130E1462`) | MSI Bravo 15 B5DD | AMD Software 26.10.1 (32.0.21045.16010) | All four ceilings obeyed. Numbers in the README. |

## Expected to work, not yet confirmed

These use the same table format. Headroom runs on them, marks them "untested model" in the title area, and
asks for confirmation before the first apply.

- Radeon RX 5300M, RX 5500M in other laptops, RX 5600M, RX 5700M
- Desktop Radeon RX 5500 XT, RX 5600 XT, RX 5700, RX 5700 XT (AMD Software already offers tuning on these)

## Not supported

| GPU | Why |
| --- | --- |
| Radeon RX 6000 / 6000M series | Different table layout. Refused by the format check. |
| Radeon RX 7000 series and newer | The driver no longer accepts a registry table. |
| Integrated Radeon graphics (APUs) | Different table layout and no registry table. |
| Older Radeon (RX 500, Vega) | Different table layout. |

## Reporting your result

If you try Headroom on a model that is not in the "Tested" list, please open a *Compatibility report* issue with:

1. The output of `Headroom.exe --status` (it contains hardware IDs but nothing personal).
2. Your AMD Software version.
3. The tiles from a 20-second **Test** on Factory and again on Cool.

If the Cool test shows clearly lower speed and power than Factory, the table is being obeyed on your model
and it can move to the tested list.

If Headroom refuses your vBIOS file or your registry table, include the exact message. Please do not attach
vBIOS files to issues.

## Operating systems

| System | Status |
| --- | --- |
| Windows 11, Windows 10 version 2004 or newer (64-bit) | Supported. |
| Older Windows 10, Windows 8.1, Windows 7 | Not supported: no `pnputil /restart-device` and no per-process GPU counters. |
| Linux | Not supported and not needed. The `amdgpu` driver exposes these controls directly (see CoreCtrl, LACT). |
