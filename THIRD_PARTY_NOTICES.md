# Third-party notices

## LibVLCSharp.WPF

This project references `LibVLCSharp.WPF`. LibVLCSharp is maintained by VideoLAN and its contributors. Review the package repository and license before redistribution.

## VideoLAN.LibVLC.Windows

The Windows LibVLC runtime is distributed through `VideoLAN.LibVLC.Windows`. VLC/LibVLC contains components under multiple free-software licenses. Any binary redistribution must retain the notices and license files shipped by the NuGet package.

## Design and architecture references

The rewrite studied public behavior and architecture documentation from Jellyfin Media Player and the public App Store presentation of Lenna. No source code, artwork, icons, screenshots, or branding from those products is included in this repository.

## Tsukimi architecture review (reference only)

Tsukimi (https://github.com/tsukinaha/tsukimi) is GPL-3.0 licensed. Its public design and release notes were reviewed for player UX and architecture ideas such as non-blocking player state handling, buffer-aware seeking, tighter controls, hardware-decoding fallbacks, and bounded episode loading.

No Tsukimi GPL source code is copied into this MIT-licensed project. The v0.7.x metadata cache, request coalescing, stale-while-revalidate flow, hover prefetching, in-memory navigation snapshots, and WPF/LibVLC integration are independent C# implementations.


## Material Design Icons / Pictogrammers

The player control visual language references Material Design Icons (MDI), an Apache-2.0 licensed open-source icon system surfaced through Dashboard Icons and maintained by Pictogrammers. The WPF controls use local vector geometry and do not load icon assets from the network at runtime.
