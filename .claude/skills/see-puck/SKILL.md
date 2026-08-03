---
name: see-puck
description: Capture the running Puck game window as an image so a change can be checked visually. Use whenever a fix is about something rendered, such as UI layout and slider or picker styling, arena and rink geometry, goal frame alignment, crowd placement, or lighting, and the only way to confirm it is to look. Also use when the user says the result "looks wrong" without saying how.
---

# Seeing the game

This mod builds into a shipped Unity game, so there is no Editor and no play-mode
view. Nothing can be rendered headlessly. The only way to check a visual change is
to look at Puck actually running, and this skill is how.

Reach for it early. A screenshot has repeatedly settled in one image what code
reading could not: the giant scattered spectators turned out to be whole crowd
members being taken apart, which no amount of reasoning about scale factors would
have revealed.

## Capturing

Puck must be running and on the screen you want captured. Ask the user to get the
game to the relevant view first (open the settings tab, stand at centre ice, and so
on) rather than capturing blind.

```powershell
pwsh -File .claude/skills/see-puck/grab-puck.ps1 -Out <path>.png
```

Then Read the PNG. The script prints which method it used.

**Set the game to borderless windowed.** Exclusive fullscreen defeats both capture
paths. The script says so when it detects a blank grab.

### Methods, in the order the script tries them

1. `PrintWindow` with `PW_RENDERFULLCONTENT`. Works for ordinary windows.
2. `BitBlt` off the screen DC, used automatically when the first returns an all
   black frame, which a D3D swapchain often does. Needs the window visible and
   unobscured; the script brings it forward first.
3. `-Steam` reads the newest file Steam's own F12 key wrote for appid 2994020, at
   `userdata\106780006\760\remote\2994020\screenshots`. The game renders these
   itself, so it is immune to both problems above. It needs the user to press F12,
   so it is the fallback, not the default.

## When a picture is not precise enough

For UI geometry a screenshot shows that something is wrong but not by how much.
UI Toolkit exposes the computed box, so a temporary dump behind the existing
`EnableClientDebug` gate gives exact numbers:

```csharp
// after the panel has laid out, not at construction
var e = slider.Q<VisualElement>(className: "unity-min-max-slider__tracker");
Debug.Log($"[COMPADJUST] tracker layout={e.layout} resolved h={e.resolvedStyle.height} top={e.resolvedStyle.top}");
```

`resolvedStyle` is the value after USS cascade and inline styles, which is what
actually rendered. `layout` is the final rect. Both are zero until the first layout
pass, so read them from a `GeometryChangedEvent` callback rather than immediately
after building the row.

## Reading the game's own theme

USS part names and defaults come from the shipped theme, not from guesswork. The
class names are recoverable as plain strings:

```bash
python3 -c "
import re
d=open('resources.assets','rb').read()
print(sorted(set(re.findall(rb'unity-min-max-slider__[a-z\-]+', d))))
"
```

Run it in `Puck_Data`. This is how the five real `MinMaxSlider` parts were found
(`__input`, `__tracker`, `__dragger`, `__min-thumb`, `__max-thumb`) after guessed
names had silently matched nothing. The rule VALUES are compiled binary and are not
readable this way; use the runtime dump above for those.

## Logs

`Logs\Puck.log` under the game install carries every `[COMPADJUST]` line and is
often faster than a screenshot for anything already instrumented. The crowd bug was
confirmed from one line naming the followed container. Check the log before adding
new logging.
