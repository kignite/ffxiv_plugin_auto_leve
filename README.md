# autoLeve

`autoLeve` is a semi-automatic crafting leve helper for **Old Sharlayan**.

Current target flow:
- A NPC (attack1): accept target leve
- B NPC (attack2): turn in item
- Loop: `A -> B -> A -> B ...`

---

## Main Window Usage

### Standing Position Reference

Use this corner standing position before starting:

![Old Sharlayan standing position](Data/standing-position.png)

### Required One-Time Manual Calibration

Before running auto loop, you must manually locate the **Highland Tea** inventory slot once:
- Open the turn-in flow manually.
- Use **Confirm Operation** to submit **Highland Tea** one time.
- This one-time action is required for slot calibration (定位高山茶位置) before automation.

1. Stand at the corner position near both NPCs in Old Sharlayan.
2. Target NPC A, click `標記目前目標為 attack1(NPC A)`.
3. Target NPC B, click `標記目前目標為 attack2(NPC B)`.
4. Set `目標理符名稱` (for example: `治癒身心的茶`).
5. Set `目標繳交次數 (0=不限)`.
   - `0` means unlimited loop.
6. Confirm that the one-time manual calibration above is already done.
7. Click `開始自動循環 (A↔B)`.
8. Click `停止` anytime to stop.

---

## Config Window

The Config window keeps low-level options (delay, callbacks, verbose, etc.).
For normal usage, control the workflow from the main window.

---

## Slash Commands

- `/alevetest semi on`
- `/alevetest semi off`
- `/alevetest semi start`
- `/alevetest semi stop`
- `/alevetest semi status`
- `/alevetest semi dump`

---

## Build

```bash
dotnet build
```

Output:
- `autoLeve/bin/x64/Debug/autoLeve.dll`
