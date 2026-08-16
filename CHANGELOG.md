## 0.8.2.24
- Card images now use aspect-correct letterboxing (Stretch=Uniform) on the dark tile instead of hard-cropping. Fallback artwork with mismatched aspect ratios (16:9 backdrops in 2:3 poster tiles, series posters in 16:9 episode tiles) previously lost most of the image to cropping; they now display complete with dark bars.

## 0.8.2.23
- Resilience against flaky (often shared) Emby servers that answer image requests with 500/404:
  - Image downloads are no longer retried — a failing image previously fired three requests each (attempt + 2 retries), tripling load and error counts.
  - Metadata refresh failures now fall back to the last cached snapshot, however old, so a server hiccup never blanks a page that was shown before; only endpoints with no history at all surface an error.
  - Main-page errors switched from a blocking modal MessageBox to a non-blocking toast that auto-dismisses after 4.5 seconds (click to close).

## 0.8.2.22
- Cards now show a lightweight rotating spinner while their image is loading over the network; the neutral "no artwork" glyph only appears after the load definitively fails.
- Documented the existing Shift + mouse-wheel horizontal scrolling for card rows (home shelves, detail shelves) in the README shortcut list; plain wheel keeps scrolling the page vertically.

## 0.8.2.21
- Fixed the "blank poster tile" root cause: episode cards requested the episode's own Primary still, but the server answered 404 ("does not have an image of type Primary") or even 500 for episodes without stills — while the detail page showed the series backdrop, which is a different image. Episode cards now fall back to series/season artwork, matching official clients.
- Failed image lookups are now negatively cached in memory (404 for 10 minutes, other failures for 90 seconds). Previously every scroll re-requested the same hundreds of missing images (282 duplicate failures observed in one session's log).
- Confirmed the "Object reference not set" text in image error logs is the Emby server's own 500 response body, not a client exception.

## 0.8.2.20
- Netflix-inspired visual restyle: near-black `#141414` page, red `#E50914` brand accent, quiet dark panels; login/server/detail pages follow the shared theme automatically.
- Cards are borderless Netflix tiles (6px radius) with a smooth 150ms hover scale-up and a shadow that only exists while hovered; watched-progress bars are Netflix red.
- Sidebar restyled to a dark rail with a red brand badge; headers, search field and busy overlay match the new palette. The green Emby logo tile placeholder is replaced by a neutral "no artwork" glyph.
- Poster loading gains an explicit fallback chain Primary → Thumb → Backdrop, so unscraped library items without a Primary image no longer render blank tiles.
- Dev tooling: new `dev.ps1 watch` live-reload mode (dotnet watch, auto restart on rude edits) and a new developer workflow guide `docs/开发工作流.md`; verify now auto-repacks stale publish output.

## 0.8.2.19
- Search results are grouped by type (电影 / 剧集 / 单集 / 合集 / 人物 / 其他) with per-section counts instead of one flat poster grid.
- Search now includes Person results; clicking a person opens their works as a browse list (back button returns to the search results).
- Searching no longer clears the browse back stack: 返回 restores the previous browse context and scroll offset.
- Ctrl+F / Ctrl+K focuses the search box from anywhere — including the detail page, which is closed first — and selects the existing text.
- Empty search results now show「没有找到相关内容」.
- Grouped sections reuse the poster card template, progressive rendering and viewport-driven image scheduling (per-section visibility), so a 150-result page stays as cheap as the flat grid.
- New one-stop dev tool `dev.ps1` / `dev.cmd`: build / run / pack (self-contained + dist zip) / verify (integrity, version match, LibVLC natives, 8s smoke launch) / clean. CI artifact name now follows the csproj version instead of the hardcoded v0.2.1.

## 0.8.2.17
- Fix regression where Audio / Subtitle / Speed / Media Source selector popups immediately closed and appeared unable to open.
- Restore native WPF ComboBox mouse capture; do not call Mouse.Capture(null) after DropDownOpened.
- Preserve one-click switching by screen-position hit-testing the intended selector/player button while an older selector owns capture.
- Clicking the currently open selector closes it; clicking a different selector closes the old one and opens the new one in the same physical click.

## 0.8.2.16
- Fix selector popup mouse-capture swallowing the first click on another player control.
- Open selector popups now release WPF mouse capture and are closed by the player without consuming the target click.
- Switching Audio / Subtitle / Speed / Media Source, or clicking Episode / Fullscreen / playback controls while a selector is open, now works in the same mouse press.
## v0.8.2.13
- Optical-align the play triangle without changing its 56x56 hit target.
- Stabilize Chinese text rendering across browse cards, episode list and player selector popups.
- Remove parent-level popup/title effects that rasterized text and caused blur on some DPI/GPU configurations.

# v0.8.2.11

- Fixed episode-panel current-item auto-centering compile errors.
- Use ScrollViewer as the WPF coordinate target for TranslatePoint.
- Use the existing LogService.Warning API for fallback logging.

v0.8.2.8 — Redesigned player control bar from approved mockup without changing playback/API logic. Double-chevron seek icons, cleaner right controls, larger invisible click targets, no default shadow boxes.

v0.8.2.7 — Seek ±10s glyphs are now mathematically mirrored: identical circle, stroke, arrow geometry and center label placement.

v0.8.2.6 — media source selector uses a fixed switch icon; no misleading 720/1080/4K badge.

v0.8.2.5 Media Source Switching

- Player can switch between Emby PlaybackInfo media versions/sources while preserving playback position and state.

v0.8.2.4 Keyboard & Interaction Polish

- Added market-standard player keyboard controls based on Netflix/YouTube conventions.
- Made audio/subtitle/speed selector hit behavior deterministic and kept controls visible while aiming.
- Removed the parent DropShadowEffect from the episode panel so text is no longer raster-blurred.
- Tightened episode typography with pixel rounding and display text formatting.

v0.8.2.3 Track Click Seek Fix

v0.8.2.2 Right-control hit targets

- Expanded right-side player controls to 54×50 DIP hit targets.
- Removed dead gaps between audio, subtitle, speed, episode/quality, and fullscreen controls.
- Kept glyphs visually restrained while making the entire transparent control surface clickable.
- Added full-area hover/pressed feedback for easier targeting on high-DPI displays.

v0.8.2.1 Immediate click-to-seek

- Progress-rail single click now commits SeekToAsync immediately on MouseDown.
- Thumb drag remains DragStarted/DragDelta/DragCompleted.
- Removed the fragile pseudo-drag MouseCapture/MouseUp path for simple rail clicks.

v0.8.2.0 Seek + video hit-test fix

## v0.8.1.9 - Click-to-seek reliability

- Fixed timeline track clicks that changed the visual position but did not always commit playback seek.
- Added Window-level PreviewMouseLeftButtonUp fallback for WPF Slider Track/RepeatButton mouse-capture edge cases.
- Track drag remains DPI-safe and uses the same SeekToAsync path as Thumb dragging.

## v0.8.1.9 - Seek preview DPI/stability fix

- Fixed seek preview time drift/jitter under Windows DPI scaling.
- Hovering the current playhead now previews the exact current Slider value.
- Win32 cursor polling no longer competes with WPF hover preview updates.

## v0.8.1.7 - Episode panel hotfix

- Restored the missing `EpisodeItemButton` XAML resource used by the lazy-created episode list.
- Added a full PlayerWindow StaticResource resolution validation step.

# v0.8.1.6

- Fix picture click hit testing: any non-interactive area inside VideoView can toggle play/pause, while title/control/episode chrome remains excluded.
- Remove native WPF ToolTip popups from the playback surface to eliminate white hover boards.
- Refine episode panel into a narrower translucent floating overlay with clearer active-row treatment.
- Move network throughput into the right-side utility group and reduce its visual priority.
- Lower the bottom control chrome and improve spacing between playback, seek, and volume groups.

# v0.8.1.5

- Removed the artificial 270px bottom cinematic black gradient from PlayerWindow.
- Fullscreen and windowed playback now preserve the original video luminance at the bottom of the frame.
- Playback controls still rely on their own text/icon shadows and the existing 3-second auto-hide behavior.

# v0.8.1.4

- Move the playback title to the top-center and increase its visual weight.
- Replace the volume capsule thumb with a compact circular white thumb and slimmer rail.
- Redraw rewind/forward 10-second controls as full circular rings with clear direction arrows.
- Keep playback, seek, MVVM, and Emby transport behavior unchanged.

# v0.8.1.3

- 清理播放器全屏实现遗留的未使用字段 `_previousWindowBounds`，消除启动/编译时 `CS0169` 警告。
- 不改动播放、Seek、全屏、进度条、音量和 UI 行为。

# v0.8.1.2

- 修正播放器底部布局顺序：进度/时间区域在上，播放控制栏在下。
- 播放进度条移除液态渐变、蓝白发光和多层高光，只保留纯红色已播放轨与白色未播放轨。
- 播放头改为简洁白色 Thumb；保留拖拽、时间预览和 Seek 逻辑。
- 缓冲轨不再叠加第三种颜色，避免视觉干扰。

# v0.8.1.1

- 修复全局鼠标轮询误触发播放/暂停：只有 Windows 命中 LibVLC 原生视频子句柄时才允许建立点击手势。
- 桌面、其他应用、窗口外、标题栏、控制栏、Popup 和剧集面板点击均不会切换播放状态。
- 鼠标按下与松开都必须位于视频句柄内，拖出画面后自动取消点击候选。

## v0.8.1 — Liquid progress UI

- Reworked player bottom panel from the supplied visual reference: toolbar above, liquid-glow seek rail below, node-following timestamp and capsule volume slider.
- Kept playback, MVVM commands, seek behavior, controls auto-hide, episodes/audio/subtitles and fullscreen logic unchanged.

# Changelog

## 0.8.0

- PlayerWindow UI presentation state migrated to `PlayerViewModel : INotifyPropertyChanged`.
- Added `RelayCommand` and ICommand bindings for play/pause, ±10s seek, mute, fullscreen, close and minimize.
- Top and bottom fullscreen buttons now share the exact same command/path.
- Fullscreen now applies the active monitor's physical `rcMonitor` through Win32 `SetWindowPos(HWND_TOPMOST)` and PerMonitorV2 DPI awareness, avoiding taskbar/work-area gaps.
- Removed the old code-behind 2.8s chrome timer. ViewModel now owns an exact 3 second inactivity timer; mouse movement immediately restores controls with a short compositor opacity transition.
- Seek Slider now uses WPF Track/Thumb as the only value owner. The native cursor poll only moves the timestamp preview and never writes the seek value.
- Seek mapping is calculated from the real `PART_Track` and Thumb width, preventing the progress dot from drifting or jumping during drag.
- Timestamp preview follows the OS cursor horizontally for the entire drag, even when the pointer slips above/below the thin rail.
- Progress and volume templates were rebuilt with full-width Track geometry and stable fixed-size Thumb containers so endpoints are not clipped.
- Bottom media title is now centered against the entire player width instead of the leftover space between asymmetric left/right controls.
- Player manifest enables PerMonitorV2 DPI awareness.
- Fullscreen entry no longer performs a synchronous `DwmFlush`; normal entry is one native `SetWindowPos` and only re-applies monitor bounds if WindowChrome/DPI actually changed them on the next render.
- Window `SizeChanged` now re-runs responsive player metrics, so fullscreen/window resize updates volume width and centered-title constraints immediately.
- Bottom title reserves the larger of the left/right control-group widths on both sides, keeping the title mathematically centered instead of visually drifting.
- Seek timestamp preview now uses the real OS cursor position throughout an active drag; WPF Track/Thumb remains the sole owner of the playhead value.
- Delivery validation caught and removed a duplicate `case Key.Down` switch label before packaging.

# 0.7.4.1

- Fixed WPF MC4111 in `SeekThumb`: template triggers now target the named `Ellipse` instead of an inner `ScaleTransform`.
- Hover/drag feedback still enlarges the seek dot inside a fixed 20x20 Thumb, so Track layout remains stable.

# Changelog

## 0.7.4.1 - Fullscreen / timeline interaction fixes

- Disabled WPF's built-in white ScrollBar right-click context menu globally.
- Replaced maximize-style fullscreen with true current-monitor fullscreen using monitor bounds; taskbar/chrome are covered and restored on exit.
- Removed user-facing cache target seconds from loading/buffering messages and diagnostics.
- Rebuilt seek and volume sliders with safe endpoint insets and stable thumb layout.
- Fixed seek playhead jitter by preventing WPF drag and Win32 fallback from writing the slider simultaneously.
- Seek now commits the last stable target timestamp on release.
- Fixed the duplicated responsive-layout local variable in the 0.7.3 source.

## 0.7.3 - Player icon system and low-latency feedback

- Unified the primary playback controls around a consistent Material Design Icons visual language instead of mixing Unicode glyphs and hand-drawn controls.
- Rebuilt ±10 second actions as compact vector controls with balanced 44 px hit targets.
- Reworked the speaker/volume control and safe endpoint padding so the full rail and thumb remain visible at 0% and 100%.
- Re-rendered the seek thumb with anti-aliased geometry, a restrained white halo and hover/drag sizing.
- Grouped current/total timestamps at the right side of the timeline to remove the isolated left timestamp block.
- Replaced the large play/pause feedback badge with a lighter 74 px vector overlay using BitmapCache and render-transform-only animation.
- Increased native LibVLC surface cursor polling from 33 ms to 16 ms at input priority.
- Play/pause gestures now paint first, yield one WPF render pass, then invoke LibVLC; the visual response is no longer delayed by the player call.
- Replaced Task.Delay-based gesture animation with a 245 ms keyframe animation that is interruptible and composition-friendly.


## 0.7.2 - Episode image identity fix

- Fix episode shelves incorrectly preferring shared Series/Season backdrop artwork.
- Episode thumbnails now request `/Items/{episodeId}/Images/Primary` using each episode's own Primary image tag.
- Add a dedicated episode image-cache path so parent artwork can never alias multiple episodes.
- Detail episode shelf uses the series Episodes endpoint to preserve per-episode image metadata.


## 0.7.1 - Player interaction polish

- Reworked ±10 second controls into cleaner circular seek icons with consistent Netflix-like spacing and hit targets.
- Rebuilt the volume control with a vector speaker glyph, complete rail, safe end padding and a larger thumb so 0%/100% no longer clip.
- Enlarged the seek thumb and added a white ring/shadow so the current timestamp is visually obvious over bright or dark video.
- Seek-time preview now follows the real OS cursor through a Canvas overlay; a Win32 cursor fallback keeps it aligned even around LibVLC's native child HWND.
- Added a native-mouse fallback for dragging the timeline when WPF mouse capture is lost; release commits exactly one timestamp seek.
- Direct-stream seeking now verifies the real LibVLC timestamp after the request and automatically falls back to Emby's StartTimeTicks compatibility stream when the source ignored the seek.
- Single-clicking the video canvas toggles play/pause and shows a short centered animated feedback badge; chrome, timeline and episode controls are excluded from this gesture.
- Simplified the visible network pill to receive speed only. Cache seconds and bandwidth ratios remain diagnostic tooltip data instead of cluttering the normal player UI.
- Player windows are now owned by the main detail/library window; closing/backing out immediately reactivates the existing parent window instead of leaving the user to reopen it.
- Kept the episode drawer text-first in this release to avoid introducing thumbnail HTTP work into the new cache-first navigation path.

## 0.7.0 - Cache-first navigation and predictive prefetch

- Added an optional Emby WebSocket listener for push-driven metadata invalidation. `LibraryChanged` and `UserDataChanged` mark snapshots stale without deleting them, so the next read stays instant and revalidates in the background.
- Lengthened normal metadata freshness windows because server push is now the primary change signal; TTL refresh remains the fallback when websocket is unavailable.
- Added bounded 50-item episode paging and player volume-wheel adjustment, independently implemented in C# after reviewing Tsukimi's public behavior/release notes.
- Added a server/user-scoped stale-while-revalidate metadata cache with bounded memory and disk storage.
- Added request coalescing so navigation, hover prefetch and foreground reads share one in-flight HTTP request per endpoint.
- Memory hits reuse typed C# objects and avoid repeated JSON deserialization.
- Added 180 ms card-hover metadata prefetch for details and browse children.
- Added detail-page PlaybackInfo pre-negotiation and 90-second reuse so Play does not duplicate negotiation when the user has already spent time on the detail page.
- Browse Back now restores an in-memory list snapshot and vertical scroll position without issuing HTTP requests.
- Busy overlays are delayed by 180 ms to avoid flashing on cache hits while still appearing for real network waits.
- Playback close invalidates only progress-derived Item/Resume metadata instead of flushing stable library/image caches.
- Added optional metadata-cache HIT entries to Network Inspector while F12 capture is active, and clarified that its timing note is client-side diagnostics rather than an Emby server field.
- Disk cache freshness is stored independently from file mtime so LRU touching cannot accidentally make stale data look fresh.
- Reviewed Tsukimi's public architecture/release notes for applicable ideas; no GPL source code was incorporated into this MIT project.

## 0.6.4 - Live network telemetry, robust seek and episode controls

- Added live receive-rate telemetry from LibVLC media read statistics, displayed as KB/s / MB/s with smoothing for bursty HLS traffic.
- Added a simple bandwidth-headroom indicator against the current Emby media bitrate (`充足 / 够用 / 吃紧`) and a forward-cache readout in seconds.
- Replaced native white Windows tooltips in the player with a dark cinematic tooltip style.
- Added a dark seek-time preview bubble; clicking or dragging anywhere on the timeline now continuously previews the target time and commits the seek on mouse release.
- Direct streams use LibVLC native seeking when seekable; non-seekable streams automatically fall back to an Emby timestamped HLS stream using `StartTimeTicks`.
- Episode controls are now hydrated in the background when lightweight list data omitted Series/Season identifiers.
- Episode detection is more tolerant of Emby-compatible servers, and the episode list button remains available when the current item is clearly episodic.
- Moved previous/next episode actions into the episode drawer to reduce primary-control clutter.

## 0.6.3 - Buffer-aware player UX and smoother streaming

- Added a Netflix-style centered loading spinner driven by the real LibVLC `Opening`, `Buffering` and `Playing` lifecycle instead of leaving a frozen black video surface.
- Added live network-cache percentage and target cache duration while buffering. The UI reports LibVLC cache fill honestly rather than inventing a whole-file download percentage.
- Added a buffered-ahead rail on the seek timeline based on the active LibVLC network cache window.
- Added adaptive VOD network cache: at least 3.0 s for direct/direct-stream playback and 4.5 s for HLS/transcoded playback, while respecting larger user-configured values.
- Moved preferred external subtitle attachment until after video playback starts so subtitle delivery can no longer delay first frame.
- Playback recovery (hardware decode -> software decode -> Emby compatibility HLS) now stays inside the loading surface; red error banners are reserved for final failures.
- Added detail-page playback preparation feedback while `PlaybackInfo` / media-source negotiation is in flight.
- Rebalanced the bottom controls: quieter icon-first audio/subtitle/speed/episode/quality actions, centered media title, separate current/total time and less desktop-form chrome.
- Episode switching now shows a proper loading state while the next `PlaybackInfo` is requested.

## 0.6.2 - Playback compatibility recovery

- Restored the v0.5 generic Emby HLS compatibility fallback instead of relying only on `SupportsTranscoding`/`TranscodingUrl` flags.
- Added automatic per-media recovery: hardware decode -> software decode -> Emby HLS compatibility stream -> final direct/software retry.
- Restored v0.5 subtitle startup behaviour: only the preferred/default external subtitle is attached before playback; unrelated subtitle endpoints can no longer block media startup.
- Reduced player UI timer pressure from 125 ms to 250 ms to leave more UI/render budget for the LibVLC video surface.
- Kept the Netflix-style UI, adaptive window sizing, episode navigation and modern controls.


## 0.6.1

- 修复剧集侧栏 `ProgressBar.Value` 默认双向绑定只读 `EpisodeOption.Progress` 导致播放器界面异常。
- 剧集观看进度显式改为 `Mode=OneWay`，只负责从 Emby 数据渲染，不再尝试向只读模型回写。
- 重新扫描播放器与主界面的 `ProgressBar` / `Slider` 数据绑定，确认其他只读进度绑定已显式使用单向模式或由代码直接控制。

## 0.6.0

- 播放器改为能力/数据驱动：按 PlaybackInfo 与用户 Policy 自动显示可用功能。
- 新增剧集侧栏、上一集/下一集、自动连播、动态音轨/字幕、静音。
- 修复运行时总时长为空、控件自动隐藏后无法唤回、时间轴拖拽反馈不连续。
- 播放补充数据后台加载，失败独立降级，不再拖垮视频播放主链路。
- 首页/详情滚动改为高精度 Delta + 阻尼插值。
- 新增 Shows/{id}/Episodes/Seasons 兼容调用并保留 ParentId 回退。


## 0.5.0

- Replaced the modal detail overlay with native in-window page navigation and a single Back action.
- Removed whole-page realtime BlurEffect from detail navigation and removed DropShadowEffect from repeated media cards.
- Replaced decorative large-radius blur glows with static radial gradients.
- Detail page now renders immediately from list metadata, then progressively enriches canonical Item / parent Series / artwork / cast / season episodes / similar items.
- Added full detail metadata models: chapters, people, external URLs, provider IDs, production locations, file metadata, premiere/date-created, media technical fields and user play count.
- Added Item Logo support including Episode parent Logo fallback.
- Added Similar Items endpoint and current-season episode shelf.
- Added cast headshots via the existing prioritized image pipeline.
- Added full media-source inspector in the detail page: video codec/profile/bit depth/fps/range/bitrate, audio tracks, subtitles, size/container/protocol/path and playback capabilities.
- Added studios, production locations, external links, resume status and chapters to the detail page.
- Detail return keeps the previous Home/Browse page alive instead of reloading it.
- Playback window opens above the detail page without closing or mutating detail navigation state.
- Added viewport-aware detail shelves: cast/episode/similar artwork is not queued until its section approaches the viewport.
- Added image-request cancellation and priority promotion: stale Home/Browse downloads are aborted when entering detail, while prefetched images can be promoted when they become visible.
- Added an advanced metadata section for Item/server/parent IDs, date-created, source dimensions, permissions, provider IDs, display/presentation identifiers and ETag.
- Expanded selected media-source details with color metadata, source type/runtime/default stream indexes and subtitle delivery characteristics.
- Network Inspector body capture is now opt-in while the inspector is open; normal browsing no longer reads and pretty-prints every JSON response a second time.

## 0.4.1

- Fixed black detail hero for Episode items that have no own Backdrop/ImageTags.
- Added ParentBackdropImageTags / ParentBackdropItemId / SeriesPrimaryImageTag model support.
- Episode artwork now falls back to the parent Series backdrop/poster using the correct parent item id.
- Sparse Episode details now fetch and cache the parent Series metadata only when needed.
- Episode synopsis, genres and community rating fall back to Series metadata instead of showing an empty panel.
- Detail metadata now includes season name when available.
- Parent metadata enrichment failure is non-fatal and never blocks playback.

## 0.4.0

- 图片系统改为 viewport-first 架构，不再进入页面就批量请求全部封面。
- 新增 Immediate / Visible / Prefetch 三档图片优先级与 8 worker 全局调度器。
- 新增同图请求合并，避免同一资源并发重复下载。
- 图片请求统一到 256 / 512 / 1024 / 1600 尺寸档位，提高跨页面缓存命中率。
- 图片请求质量调整为 82；所有手工 HttpRequestMessage 显式请求 HTTP/2（可回落 HTTP/1.1），图片请求使用 image/* Accept。
- 图片解码独立限制为 2 并发，降低 WPF/WIC 内存尖峰风险。
- 内存图片缓存改为约 220 MiB 上限的 LRU；磁盘缓存增加约 768 MiB 上限自动清理。
- 首页横向列表根据真实滚动视口加载图片，并仅预取下一小段。
- 浏览页先渲染 48 张卡片，靠近底部再分批追加 36 张，降低大列表视觉树压力。
- 首页按钮默认复用 5 分钟 Latest 缓存，不再每次点击都强制重新请求。
- Emby Logo 本地植入 EXE/窗口、服务器卡片、登录页、主侧栏、播放器和媒体占位符。
- Emby 图标来源及 Apache-2.0 说明加入第三方声明。

## 0.3.1

- 精简首页/列表 Fields，移除 MediaSources/MediaStreams 等重字段。
- 首页改为分区渐进加载，并加入 Latest 5 分钟内存缓存。
- 关闭播放器只刷新继续观看，不再强制主窗口跳回首页或整页重载。
- Saved profile 的 `/Users/{id}` 校验增加 5 秒软超时。
- 详情页优先使用 Item 自带 MediaSources，仅在缺失时回退 PlaybackInfo。
- PlaybackInfo 使用真实续播 StartTimeTicks。
- 尊重 AddApiKeyToDirectStreamUrl，保护服务器签名直链。
- 媒体版本显示更多分辨率/编码/帧率/音频信息。
- 播放状态上报映射到 Emby stream index。
- 自动挂载默认外置字幕 DeliveryUrl。
- Network Inspector 修复 JSON 内嵌 URL 的 api_key 泄漏，并对签名直链的 sig/signature 脱敏。
- 首页/浏览图片加载增加跨分区共享的 4 并发节流，避免多个区块同时排队过多图片任务。

## 0.3.0

- 网络调试器重做为 DevTools Network 风格：请求表格、类型过滤、搜索、Headers / Payload / Response / Timing 详情页和可拖动分栏。
- JSON 调试输出按响应字符集读取，并使用不转义中文的 JSON encoder，`\uXXXX` 中文内容直接显示为可读文本。
- 新增复制 cURL、复制接口详情和复制响应，便于将真实 Emby 接口数据用于后续 UI 定制。
- 全局滚动条改为低干扰中性样式，移除高亮玻璃阴影和突兀动画。
- 全面重做 WPF 视觉系统，采用深色半透明水玻璃风格。
- 播放器改为沉浸式视频画面与悬浮玻璃控制坞。
- 音轨、字幕、倍速、音量、转码和全屏均使用彼此独立的控件与焦点状态。
- 媒体详情由右侧白色抽屉改为居中的水玻璃详情浮层。
- 详情浮层打开时对主内容应用背景模糊和降亮度。
- 播放器由 Owner + ShowDialog 模态窗口改为独立非模态窗口。
- 主窗口与播放器的最小化、还原和焦点状态不再绑定。
- 同一时间只保留一个播放器实例，重复点击播放会激活已有播放器。
- 播放器加入自定义玻璃标题栏和独立窗口控制按钮。

## 0.2.5

- 修复首页媒体卡片 `ProgressBar.Value` 默认双向绑定只读 `ProgressPercent` 时触发的 WPF 绑定异常。
- 海报卡片和横向卡片的播放进度均显式改为 `Mode=OneWay`。
- 扫描现有 XAML 的 `Value` 绑定，未发现其他同类只读属性回写问题。

## 0.2.4

- 修复首页三组图片并发加载时最多同时完整解码 18 张原图，可能造成内存瞬时暴涨和进程闪退。
- 图片按界面目标宽度缩放解码，不再把海报和背景图以原始分辨率全部展开到内存。
- 图片下载与解码改为全局最多 4 个并发，并限制异常超大图片。
- 捕获图片解码内存不足，主动释放部分内存缓存，避免单张异常图片终止程序。
- 普通 WPF UI 未处理异常改为记录日志并提示，不再立即关闭整个程序。
- 启动日志增加版本、系统及运行时信息，便于继续定位原生崩溃。

## 0.2.3

- 修复部分 Emby 服务器将 `Studios` 返回为对象数组时首页 JSON 反序列化失败。
- 将当前未使用的 `Studios` 改为保留原始 JSON，同时兼容对象、字符串和空值。
- 从通用首页字段中移除当前未使用的 `Studios`，降低不同服务器版本的响应差异。

## 0.2.2

- 修复 LibVLCSharp `NetworkCaching` 属性要求 `uint` 导致的 CS0266。
- 修复 `string.StartsWith` 错用 `char + StringComparison` 重载导致的 CS1503。
- 对网络缓存配置做非负保护，避免旧配置中的负数造成转换异常。

## 0.2.1

- 修复 WPF `MediaPlayer` 与 LibVLCSharp `MediaPlayer` 的类型名冲突（CS0104）。
- 修复发布脚本忽略 `dotnet` 失败退出码、错误显示成功的问题。
- 发布成功前额外检查 `EmbyRemotePlayer.exe` 是否真实生成。

## 0.2.0

- 完整重写主界面与应用导航。
- 新增多服务器配置及 Windows 凭据管理器令牌存储。
- 新增继续观看、最新电影、最新剧集、收藏和搜索。
- 新增媒体详情抽屉及多媒体源选择。
- 重写播放计划生成逻辑，加入直连优先与自动转码回退。
- 新增播放器启动超时检测、前后 10 秒、倍速、音轨、字幕和快捷键。
- 新增请求重试、取消、图片缓存、原子配置写入和日志。
- 将代码拆分为 Models、Services、ViewModels、Views。
