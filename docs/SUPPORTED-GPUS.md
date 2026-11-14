# Supported GPUs

Headroom checks the GPU's power table format, not its name. If the table isn't the RX 5000 format, Headroom
won't change anything.

## ✅ Tested

| GPU | Laptop | Driver |
| --- | --- | --- |
| RX 5500M | MSI Bravo 15 B5DD | AMD Software 26.10.1 |

## ⚠️ Should work, not yet tested

These cards use the same table format. Headroom runs on them, but it labels them "untested" and asks you to
confirm before the first apply.

- **Laptop:** RX 5300M, RX 5500M (other laptops), RX 5600M, RX 5700M
- **Desktop:** RX 5500 XT, RX 5600 XT, RX 5700, RX 5700 XT

## ❌ Not supported

| GPU | Why |
| --- | --- |
| RX 6000 series | Uses a different table format |
| RX 7000 series and newer | The driver ignores registry tables |
| Integrated Radeon graphics | Uses a different table format (the graphs still work) |
| Older Radeon (RX 500, Vega) | Uses a different table format |
| Nvidia, Intel | No equivalent table. For Nvidia, try MSI Afterburner. |

**Windows:** Windows 10 version 2004 or newer, or Windows 11, 64-bit. Older versions lack features Headroom
needs.

**Linux:** not needed. The `amdgpu` driver already exposes these controls. Try CoreCtrl or LACT.

## Report your card

Tried it on a card that isn't on the tested list? Please open a **Compatibility report** issue with:

1. The output of `Headroom.exe --status`. It shows hardware IDs, nothing personal.
2. Your AMD Software version.
3. The results of a 20-second **Test** on **Factory** and on **Cool**.

If Cool shows clearly lower speed and power than Factory, it works, and your card can join the tested list.

If Headroom rejects your vBIOS or registry table, include the exact error message. Please don't attach vBIOS
files.
