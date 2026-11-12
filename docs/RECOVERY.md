# Recovery

Everything Headroom does comes down to one registry value. Removing that value puts the GPU back on the
factory table in its vBIOS. Nothing is flashed, so there is nothing permanent to undo.

## 1. From Headroom

Pick the **Factory** preset and press **Apply to GPU**.

## 2. Without opening the window

Run `Reset-to-factory.cmd` (next to `Headroom.exe`), or:

```
Headroom.exe --reset
```

## 3. By hand, including from Safe Mode

Use this if Windows is unstable or the screen stays black after an apply.

1. Start Windows in Safe Mode (hold **Shift** while choosing *Restart*, then *Troubleshoot → Advanced options →
   Startup Settings → Restart → 4*). The AMD driver does not load in Safe Mode.
2. Open `regedit` and go to
   `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}`.
3. Open each numbered subkey (`0000`, `0001`, ...) and find the one whose `DriverDesc` is your Radeon GPU.
4. Delete the value `PP_PhmSoftPowerPlayTable` in that subkey.
5. Restart normally.

The same thing from an administrator command prompt, once you know the subkey number:

```
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001" /v PP_PhmSoftPowerPlayTable /f
```

## 4. Reinstall the driver

Installing the AMD graphics driver again removes the value as part of setup.

## Backups

Every apply saves the table that was set before it to `backups\` next to the exe (newest 30 kept). These are
raw copies of the registry value, useful if you want to return to an exact earlier state.
