# Recovery

Everything Headroom changes lives in **one registry value**. Delete it and the GPU goes back to factory.
Nothing is flashed, so nothing is permanent.

Try these in order.

## 1. Use Headroom

Pick **Factory** and press **Apply to GPU**.

## 2. Use the reset script

Double-click `Reset-to-factory.cmd` (it's next to `Headroom.exe`), or run:

```
Headroom.exe --reset
```

## 3. Fix it by hand from Safe Mode

Use this if Windows won't start properly or the screen stays black.

1. **Start in Safe Mode.** Hold **Shift** while you click *Restart*, then go to *Troubleshoot → Advanced
   options → Startup Settings → Restart*, and press **4**.
2. Open **regedit** and go to:
   ```
   HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}
   ```
3. Open the numbered folders (`0000`, `0001`, …) until you find the one whose `DriverDesc` is your Radeon GPU.
4. Delete `PP_PhmSoftPowerPlayTable` from that folder.
5. Restart normally.

## 4. Reinstall the AMD driver

A fresh driver install removes the value too.

---

**Backups:** each time you apply, Headroom saves the previous table in the `backups` folder next to the exe.
It keeps the newest 30.
