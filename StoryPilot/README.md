# StoryPilot

A small Genshin Impact story auto-play tool, cut down from BetterGI's "Auto Story" (AutoSkip) feature.
It only does story things:

- **Advance dialogue**: presses Space while a conversation is playing.
- **Auto-pick dialogue options**: prefers quest options (orange text or "!"), otherwise picks the first or last option.
- **Skip cutscenes**: clicks through black-screen narration and presses the hangout "Skip" button.
- **Close pop-ups after dialogue**: item and tutorial pop-ups, and full-screen pages with an X.

## Runs in the background

Keep the game **windowed**. It can sit behind other windows while you work, but it can't be minimized.

- **Seeing the game:** it uses Windows Graphics Capture, which can still see a window when other windows cover it.
- **Sending input:** it sends keys and clicks straight to the game window (PostMessage). It never moves your mouse and never brings the game to the front.
- **Choosing options:** it uses the keyboard (S / W to move, F to select), so no mouse is needed.

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
