# autoLeve (Semi-Auto Leve Assistant)

This plugin is currently focused on a semi-automatic leve workflow:
- Flow A: accept a target leve (M3-3 complete)
- Flow B: turn-in flow (still under active validation)

## Current Status

### Flow A
- Implemented path:
  - `Talk -> SelectString -> GuildLeve(select) -> Accept -> end interaction`
- Supports explicit callback-based target selection
- Supports M3-4 two-argument mode (`[cmd, leveId]`)

### Flow B
- State machine is in place:
  - `Talk -> Request(select item) -> Request(submit) -> SelectYesno -> Talk -> JournalResult`
- Main unstable area is still Step 2/3 (item selection and submit behavior)

## Debug Tools (Config Window)

### 1) Callback Capture / Replay
- `Record Next Action (Single)`
  - Captures exactly one `FireCallback`
- `Enable Generic Callback Capture`
  - Continuous callback capture window
- `Replay Last Captured`
  - Replays the latest captured callback
- `Replay Target Addon`
  - Lets you override `(unknown)` captures with a visible addon name

### 2) UI Event (DragDrop/Click) Capture / Replay
- `Record Next UI Event (Single)`
  - Attempts to bind capture on:
    - `Request`
    - `RequestItem`
    - `InventoryExpansion`
  - Monitors drag/drop + click-oriented UI events
- `Replay Last UI Event`
  - Replays the latest captured UI event (`event type + param`)

### 3) Apply Capture to Automation
- `Apply Last Capture to B Item Select`
  - Applies the latest callback capture to Flow B Step 2 selection logic

## Recommended Test Procedure

### Test B Step 2 (Item Select)
1. Open the turn-in screen (`Request` visible).
2. Click `Record Next UI Event (Single)`.
3. Manually do exactly one action: select the first item.
4. Check `Last UI Event Capture`.
5. Try `Replay Last UI Event`.

### Test B Step 3 (Submit)
1. Ensure Step 2 has successfully selected an item.
2. Click `Record Next Action (Single)`.
3. Manually press submit once.
4. Verify callback capture and replay behavior.

## Commands
- `/alevetest semi on`
- `/alevetest semi off`
- `/alevetest semi start`
- `/alevetest semi stop`
- `/alevetest semi status`
- `/alevetest semi dump`

## Build

```bash
dotnet build autoLeve.sln
```

Output DLL:
- `autoLeve/bin/x64/Debug/autoLeve.dll`

## Notes
- `FireCallbackInt` is not a typical high-level Dalamud service API method. It is from `FFXIVClientStructs` (`AtkUnitBase`) convenience wrappers.
- The same in-game action may go through callback or drag/drop event chains.
  - This is why the plugin now provides both callback capture and UI event capture paths.
