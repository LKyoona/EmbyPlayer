# Player MVVM boundary

`PlayerWindow` keeps the LibVLC HWND and Emby transport lifecycle because those are native/window resources. `PlayerViewModel` owns presentation state and user commands.

## ViewModel state

- `Title`, `BottomTitle`, `NetworkRateText`
- `PositionSeconds`, `DurationSeconds`, `CurrentTimeText`, `TotalTimeText`
- `Volume`, `IsMuted`, `IsPlaying`, `IsFullscreen`
- `IsControlsVisible`

## Commands

- `TogglePlaybackCommand`
- `SeekBackwardCommand` / `SeekForwardCommand`
- `ToggleMuteCommand`
- `ToggleFullscreenCommand`
- `CloseCommand` / `MinimizeCommand`

## Interaction rules

The ViewModel starts a 3-second inactivity timer. Real pointer movement calls `NotifyPointerActivity()`. Active seek, volume drag, combo popup, and episode drawer use `BeginInteraction()` / `EndInteraction()` so a user action is never hidden mid-gesture.

The timeline has one geometry owner: WPF `Track`/`Thumb`. Win32 cursor polling is used only to keep the timestamp bubble visually attached to the cursor over the LibVLC native child window. It never changes the Slider value.

Fullscreen is also single-path: top button, bottom button and F/F11 all reach `ToggleFullscreen()`, which moves the HWND to the current monitor's physical `rcMonitor` via `SetWindowPos`. It does not animate WPF Width/Height or synchronously wait on DWM; a next-render guard only corrects the bounds if WindowChrome/DPI actually changed them.

The centered bottom title is overlaid against the whole player width. Its max width reserves the larger left/right control group on both sides, so asymmetric controls do not visually move the title off-center.
