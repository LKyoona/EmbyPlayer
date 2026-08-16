# v0.8.2.5 Media Source Switching Validation

- PASS: PlaybackPlan retains all PlaybackInfo.MediaSources.
- PASS: player media-version selector is visible only when more than one media source exists.
- PASS: selecting a version requests fresh PlaybackInfo at the current playback position.
- PASS: selected MediaSourceId is used to build the replacement PlaybackPlan.
- PASS: source switch restarts at the current timestamp and preserves paused/playing state.
- PASS: playback speed is reapplied by MediaPlayer_Playing.
- PASS: audio preference is mapped to the equivalent stream in the selected version.
- PASS: subtitles-off state is preserved; enabled subtitles are mapped when possible.
- PASS: selector popup participates in keyboard/Escape and auto-hide interaction guards.
- PASS: XAML parse, event bindings, StaticResource resolution and ControlTemplate TargetName checks.

# 验证说明

交付前已执行以下静态检查：

- 所有 XAML 文件均通过 XML 解析
- XAML 中声明的窗口事件均能在对应代码文件找到处理函数
- 所有 C# 文件的括号、圆括号和方括号结构匹配
- 未发现合并冲突标记、硬编码账号、密码或访问令牌
- 发布脚本保留 LibVLC DLL 与 `plugins` 目录，不使用单文件发布

当前交付环境不是 Windows，且没有可用的 .NET SDK/NuGet 构建链，因此这里没有伪造“已编译通过”的结论。请在 Windows 10/11 上双击 `publish-win-x64.cmd`，或运行 GitHub Actions 的 `build-windows` 工作流完成真实编译。只有脚本显示 `PUBLISH SUCCEEDED` 才表示构建成功。

## WPF 绑定检查

- `ProgressBar.Value` 绑定只读 ViewModel 属性时必须显式使用 `Mode=OneWay`。
- 当前两个 `ProgressPercent` 绑定均已设置为单向绑定。

## v0.3.0 / v0.3.1 / v0.4.0 窗口与控件检查

- 播放器不再调用 `ShowDialog()`，也不再设置主窗口为 `Owner`。
- 主窗口只维护一个独立播放器实例，重复播放会激活已有窗口。
- 音轨与字幕分别使用独立的更新保护状态，不再共用一个 `_updatingTracks` 标记。
- 三个下拉框由各自的 `ToggleButton`、焦点触发器和弹出层组成，交互状态互不共享。
- 播放器 XAML 的自定义窗口按钮、滑块、下拉框事件均存在对应处理函数。

## Network Inspector 检查

- 调试器使用结构化请求记录，不再拼接整页日志文本。
- 每条请求可查看 URL、Method、Status、Content-Type、请求/响应 Headers、Query、Payload、Response 和整体耗时。
- `AccessToken`、密码、Authorization、Emby Token、`api_key`、Cookie 均在进入 UI 前脱敏。
- JSON 响应读取 `Content-Type` 的 charset；无 charset 时按 BOM / UTF-8 回退。
- JSON 重新格式化时使用 `UnsafeRelaxedJsonEscaping`，中文不会再次变成 `\uXXXX`。
- 图片、视频和音频二进制响应体不缓存，避免调试器因媒体流占用大量内存。
- 已专门回归检查 `TextBox` 上不存在非法 `LineHeight` 属性。


## v0.3.1 抓包驱动优化检查

- 列表字段不再包含 `MediaSources` / `MediaStreams`。
- 详情页不再无条件调用 PlaybackInfo。
- 绝对 DirectStreamUrl 在 `AddApiKeyToDirectStreamUrl=false` 时保持原签名查询串。
- PlaybackInfo 的 StartTimeTicks 使用续播位置。
- Network Inspector 会脱敏 JSON 字符串内部 URL 的 `api_key`，以及签名直链中的 `sig/signature`。
- 关闭播放器仅在主窗口仍处于首页时刷新 Resume。


## v0.4.0 加载架构检查

- 已移除旧的 `_cardImageThrottle + Task.WhenAll(全部卡片)` 图片扇出模式。
- 首页三个横向列表均绑定独立 `ScrollChanged` 事件，并按视口范围调度图片。
- 浏览页按批次向 WPF 视觉树追加卡片，不再一次渲染全部结果。
- 图片调度器存在 Immediate / Visible / Prefetch 三个队列与 8 个 worker。
- 同一图片 key 使用共享 `ImageRequestEntry` 合并并发请求，并允许 Prefetch 请求提升为 Visible / Immediate。
- 图片宽度固定分桶为 256 / 512 / 1024 / 1600。
- WPF 图片解码始终设置 `DecodePixelWidth`，并额外限制 2 个并发解码。
- 内存缓存采用有界 LRU；磁盘缓存采用有界后台清理。
- 每个手工创建的 HttpRequestMessage 显式设置 HTTP/2 + RequestVersionOrLower，并允许回落 HTTP/1.1。
- Emby PNG/ICO 已作为 WPF Resource / ApplicationIcon 写入工程；窗口和占位资源引用均可解析到项目内文件。
- XAML XML 解析、XAML 事件处理器对应关系、C# 括号结构再次通过静态检查。

本交付环境仍不能执行 Windows WPF 实机运行；真实编译请以 Windows 上 `publish-win-x64.cmd` 的 `PUBLISH SUCCEEDED` 为准。


## v0.4.1 Episode 详情继承检查

- `EmbyItem` 已映射 ParentBackdropImageTags / ParentBackdropItemId / SeriesPrimaryImageTag / SeasonName。
- Backdrop 请求的 item id 与 image tag 同源：父级 tag 必须使用父级 item id，不再错误请求单集 id。
- Primary 回退到 SeriesPrimaryImageTag 时使用 SeriesId。
- Episode 缺 Overview / Genres / CommunityRating 时才补查 Series 详情。
- Series 详情在当前服务器会话内缓存，并在 ReplaceApi 时清空，避免跨服务器污染。
- 父级元数据请求失败为非致命降级，不影响播放。
- XAML XML、事件处理器引用和 C# 括号结构再次通过静态检查。

## v0.5.0 详情页与轻量渲染检查

- MainWindow 详情不再使用 DetailOverlay / BlurEffect；MainPageContainer 与 DetailPage 互斥显示。
- 详情页返回只切换当前窗口页面，不创建第二个详情窗口、不使用 ShowDialog。
- 详情页支持 Backdrop / ParentBackdrop / Primary / Logo fallback。
- Item 模型覆盖 Chapters / People / ExternalUrls / ProviderIds / ProductionLocations / file metadata / user PlayCount。
- MediaSource 切换会独立刷新视频、音轨、字幕和文件/播放能力摘要。
- Episode 当前季、Similar Items、People 图片均为可失败的增量区块，不阻塞首屏。
- 重复媒体卡片已移除 DropShadowEffect；整页模糊已移除。
- Busy 状态改成非阻塞状态浮条，不再盖住整个内容区。
- 进入详情页会取消 Home/Browse 的旧 viewport 图片会话；图片服务会在最后一个消费者离开时取消对应排队/下载工作。
- 返回主页面时创建新的 viewport 会话，只重新调度当前可见卡片，不重新请求列表元数据。
- 演职人员、当前季和相似作品只有接近纵向视口后才调度图片，并按横向可见区区分 Visible / Prefetch。
- Network Inspector 默认不读取/Pretty-print 响应体；仅在 F12 调试窗口打开期间抓取后续请求 Body，避免正常浏览时重复处理 JSON。
- 更多信息区覆盖 Item/Server/Parent/Series/Season IDs、DateCreated、文件维度/权限、Provider IDs、DisplayPreferences、Presentation Key、ETag 等扩展元数据。
- 已扫描源码，不包含本次抓包中的服务器域名、IP、用户 ID、媒体 ID 或签名直链样本。


## v0.8.0 MVVM / Player interaction checks

- `PlayerViewModel` derives from the shared `NotifyObject` (`INotifyPropertyChanged`) and exposes the player presentation properties used by XAML.
- All primary player buttons use `ICommand` bindings: play/pause, seek backward, seek forward, mute, fullscreen, close, minimize.
- Both fullscreen buttons bind to the same `ToggleFullscreenCommand`; keyboard F/F11 uses the same `ToggleFullscreen()` implementation.
- Fullscreen bounds use `GetMonitorInfo(...).rcMonitor` + `SetWindowPos` in physical pixels; `app.manifest` declares PerMonitorV2 DPI awareness.
- Chrome visibility is driven by `PlayerViewModel.IsControlsVisible` with an exact 3-second `DispatcherTimer`; old `_controlsHideTimer` code was removed.
- `PositionSlider` binds `Value`/`Maximum` to ViewModel and uses standard Thumb drag routed events. No Win32 cursor poll writes `Slider.Value`.
- Slider pointer-to-value mapping resolves the actual `PART_Track` and Thumb width.
- ControlTemplate `TargetName` scope scan passed; no trigger references a transform or element outside its template namescope.
- XAML XML parse, XAML event-handler existence, ViewModel command/property binding surface, and modified C# structural brace scans passed in the delivery environment.
- The delivery container still has no Windows .NET SDK, so the package intentionally does not claim a real WPF compile. Run `build.ps1` on Windows; only `BUILD SUCCEEDED` is final compile acceptance.

## v0.8.0 MVVM 播放器交付前回归检查

- 4 个 XAML 与 app.manifest 均通过 XML 解析。
- XAML 声明的事件处理器逐一验证存在于对应 code-behind。
- 所有 `ControlTemplate` 的 `TargetName` 均验证在同一模板 namescope 内，防止再次出现 MC4111。
- `PlayerViewModel` 验证包含 INotifyPropertyChanged 状态及播放、±10s、静音、全屏、关闭、最小化 ICommand。
- 顶部与底部全屏按钮验证为同一个 `ToggleFullscreenCommand`，并使用 `rcMonitor + SetWindowPos` 的物理显示器路径。
- app.manifest 验证启用 `PerMonitorV2`。
- Seek/Volume 的绑定值验证不存在 code-behind 本地赋值覆盖；Win32 光标轮询不写 Slider.Value。
- 3 秒自动隐藏验证由 `PlayerViewModel` 的 DispatcherTimer 驱动。
- 检查并修复重复 switch case；检查 PlayerWindow/PlayerViewModel/RelayCommand 的结构括号。
- 检查工程不包含 bin/obj/.vs/.idea/__pycache__。

本环境没有 Windows .NET SDK/MSBuild，因此以上属于静态交付验收，不能替代 Windows 上的真实 WPF 编译。


## v0.8.1 Liquid progress UI checks

- Player control order remains Play -> Rewind 10 -> Forward 10 -> Volume.
- Playback toolbar is above the progress/time row.
- Seek thumb is an energy-node visual with fixed arranged width; no hard red dot is used.
- Current/total timestamp follows the playback node using Track geometry.
- Volume slider uses a capsule thumb and separate white/transparent rails.
- All ControlTemplate TargetName references were validated inside their own template namescopes.
- Existing MVVM commands, playback logic, 3-second auto-hide, episode/audio/subtitle controls, seek handlers and fullscreen logic remain connected.
## v0.8.2.1 click-to-seek regression check
- Clicking the seek rail commits SeekToAsync on MouseDown; it does not use pseudo-drag capture/MouseUp.
- Thumb dragging remains the only drag-seek path.
- PlayerWindow has no _trackSeekDrag or track MouseUp handlers.


## v0.8.2.2 right-control hit target regression check

- Right-side audio/subtitle/speed ComboBoxes use a 54 x 50 DIP transparent clickable surface.
- Episode/quality/fullscreen buttons use the dedicated `RightControlButton` 54 x 50 DIP surface.
- Adjacent right-side controls have no explicit dead-gap margins.
- The visible glyph remains centered and smaller than the clickable surface.
- Full hit surfaces have hover/pressed feedback.
