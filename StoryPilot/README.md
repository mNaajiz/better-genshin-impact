# StoryPilot

A small Genshin Impact story auto-play tool, cut down from BetterGI's "Auto Story" (AutoSkip) feature.
It only does story things:

- **Advance dialogue**: presses Space while a conversation is playing.
- **Auto-pick dialogue options**: prefers quest options (orange text or "!"), otherwise picks the first or last option.
- **Skip cutscenes**: clicks through black-screen narration and presses the hangout "Skip" button.
- **Close pop-ups after dialogue**: item and tutorial pop-ups, and full-screen pages with an X.
- **Teleport to next quest** (off by default): once a story dialogue ends and you're back in the normal view, it reads the quest distance under the quest name (e.g. "274m").
  - If the quest is farther than the limit (150 m by default, set under More settings), it opens the quest on the map: J, then Navigate.
  - It then teleports to the unlocked waypoint nearest the quest, but only if that waypoint is less than half as far from the quest as you are.
  - Otherwise it closes the map and does nothing.
  - Locked waypoints are skipped.
  - Pressing Ctrl+Alt+P, or using the keyboard or mouse in the game, cancels it.
- **Auto-teleport when I pick a waypoint** (on by default, separate from the story switches): on the big map, when you click a waypoint, Statue of The Seven, domain or other teleport point and its panel shows the Teleport button, StoryPilot presses it for you.
  - If several icons overlap and a pick list opens, it chooses the teleport waypoint row (or another teleport point if there's no waypoint).
  - It waits a moment first (100 ms by default, under More settings) so the panel can finish sliding in.
  - It only acts on the big map and only while the game is the window in front. It uses a real click and puts the mouse back.

"Skip cutscenes" only clicks a black screen when it's a narration screen (pure black with centred white text) and only within 20 seconds of a dialogue StoryPilot was handling. Dark caves, loading screens and your own play are left alone.

## Runs in the background

Keep the game **windowed**. It can sit behind other windows while you work, but it can't be minimized.

- **Seeing the game:** it uses Windows Graphics Capture, which can still see a window when other windows cover it.
- **Sending input:** it sends keys and clicks straight to the game window (PostMessage). It never moves your mouse and never brings the game to the front.
- **Choosing options:** it uses the keyboard (S / W to move, F to select), so no mouse is needed.

### Teleport and the background

Map clicks are sent the same background way first.
If the game ignores them, StoryPilot notices (the map or Teleport button doesn't appear), closes the map and remembers this.
From then on it teleports only while the game is the window in front, using real clicks.
The mouse is put back where it was afterwards.

## What it does *not* do (compared with BetterGI)

These are left out on purpose:

- **No BitBlt capture.** BetterGI's BitBlt mode switches off Windows' "Optimizations for windowed games".
- **No overlay window, desktop clone (RDP child session), FPS tracking (PresentMon) or hardware-monitor drivers.** These can slow down or disturb the display.
- **No OCR or AI models** (Paddle OCR, ONNX, DirectML), so nothing extra runs on the GPU or CPU.
- **Nothing is written to the registry or the game's settings.**

The only files it writes:

- **Settings:** `%APPDATA%\StoryPilot\settings.json`
- **Screenshots:** `Pictures\StoryPilot`, only when you press "Save screenshot".

## Use

1. Run `StoryPilot.exe` and accept the administrator prompt. The game runs as administrator, and Windows blocks input from apps that aren't.
2. Start the game in windowed mode. A 16:9 size works best, e.g. 1600x900 or 1920x1080.
3. Press **Start** (or Ctrl+Alt+P).

To report a screen it gets stuck on, press **Save screenshot** while the screen is showing. The image goes to `Pictures\StoryPilot`.

## Build

You need the .NET 8 SDK.

```
dotnet test StoryPilot.slnx
dotnet publish StoryPilot/StoryPilot.csproj -c Release -o <output folder>
```

## Licence

GPL-3.0, the same as BetterGI. The image templates and recognition regions come from BetterGI. The capture code is adapted from `Fischless.GameCapture`.
