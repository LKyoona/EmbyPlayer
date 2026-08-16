## v0.8.2.13
- Optical-align the play triangle without changing its 56x56 hit target.
- Stabilize Chinese text rendering across browse cards, episode list and player selector popups.
- Remove parent-level popup/title effects that rasterized text and caused blur on some DPI/GPU configurations.

v0.8.2.8 — control bar visual polish: compact bold double-chevron ±10s icons, frameless 48px hit targets, crisp right-side icons; all capability-driven visibility and playback handlers preserved.

v0.8.2.7 — rewind/forward 10-second controls use one canonical glyph with an exact horizontal mirror for optical symmetry.

v0.8.2.6 — media source selector uses a fixed switch icon; no misleading 720/1080/4K badge.

v0.8.2.5 Media Source Switching

- Player can switch between Emby PlaybackInfo media versions/sources while preserving playback position and state.

v0.8.2.4 Keyboard & Interaction Polish

Keyboard: Space/Enter/K play-pause; Left/J -10s; Right/L +10s; Up/Down volume; M mute; F/F11 fullscreen; Esc closes popup/panel or exits fullscreen; C subtitles; Shift+< / Shift+> speed; 0-9 seek 0%-90%; Home/End start/end; Shift+N/P next/previous episode when available.

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

## v0.8.1.9 - Seek preview DPI/stability fix

- Fixed seek preview time drift/jitter under Windows DPI scaling.
- Hovering the current playhead now previews the exact current Slider value.
- Win32 cursor polling no longer competes with WPF hover preview updates.

## v0.8.1.7 - Episode panel hotfix

- Restored the missing `EpisodeItemButton` XAML resource used by the lazy-created episode list.
- Added a full PlayerWindow StaticResource resolution validation step.

# Emby Remote Player v0.8.1.6

## v0.8.1.6 — 播放器交互与侧栏收口

- 视频画面点击使用 VideoView 屏幕矩形 + 交互控件排除策略，不再依赖 LibVLC HWND 命中，因此画面非控件区域单击暂停/继续更稳定。
- 播放器移除原生 WPF ToolTip Popup，避免按钮悬停出现白色系统提示板。
- 剧集面板改成更窄的半透明浮层，增加圆角、轻阴影、Hover 和当前集高亮。
- 实时网速移入右侧工具组，降低对画面的干扰。
- 底部控制栏进一步靠近屏幕底部，并重新拉开播放、快进退和音量之间的分组间距。


## v0.8.1 — Liquid progress UI

- Reworked player bottom panel from the supplied visual reference: toolbar above, liquid-glow seek rail below, node-following timestamp and capsule volume slider.
- Kept playback, MVVM commands, seek behavior, controls auto-hide, episodes/audio/subtitles and fullscreen logic unchanged.

# Emby Remote Player v0.8.1

## v0.8.0：播放器 MVVM / 真全屏 / 稳定 Slider

- 播放器主控件改用 `PlayerViewModel + INotifyPropertyChanged + ICommand`。
- 顶部与底部全屏统一为同一命令，使用当前显示器物理 Bounds 覆盖任务栏。
- 控制栏严格 3 秒无鼠标移动后淡出，鼠标一动立即恢复。
- Seek/音量 Slider 使用稳定的 WPF Track/Thumb，不再由 Win32 光标轮询改写 Value。
- 时间预览始终跟随实际光标横坐标，拖动松手后才提交 Seek。
- 底部标题按整个播放器宽度真正居中。



## v0.7.4.1：全屏 / 时间轴 / 音量交互修复

- 禁用 WPF ScrollBar 自带的白色右键菜单，右键滚动条不会再出现“滚动到此 / 左边缘 / 向左翻页”等系统菜单。
- 全屏不再使用普通 Maximized 工作区，而是读取当前显示器真实 Bounds，临时移除 WindowChrome 并覆盖任务栏；退出全屏完整恢复窗口状态。
- 缓冲界面只保留“正在缓冲”和真实百分比，不再向普通用户展示“目标 3 秒”、缓存倍数等实现细节。
- 时间轴和音量条使用固定布局尺寸的 Thumb + 安全端点留白，0%/100% 都完整显示。
- Seek 圆点悬停/拖动只缩放内部图形，不再改变 Track 的布局尺寸。
- WPF 拖动与 Win32 鼠标兜底现在互斥，杜绝两套逻辑同时写 PositionSlider 导致进度圆球乱跳。
- 拖动过程中保存独立的目标时间戳，松开后只提交最后一个稳定目标；UI 计时器在拖动期间不会回写进度。
- 音量条扩大有效轨道并支持点击轨道直接定位。

## v0.7.2：Episode 独立缩略图修复

- Episode 卡片统一使用自己的 `ItemId + ImageTags.Primary`，不再错误复用 Series/Season 公共背景图。
- 详情页本季剧集改用 `GetEpisodesAsync(seriesId, seasonId)` 获取每集自身图像元数据。
- 首页继续观看、最新剧集和详情剧集共享同一条 Episode 图片规则。
- 旧错误缓存不会继续命中，因为新请求使用每一集独立的图片缓存 key。


### v0.7.1 播放器交互

- 单击视频画面暂停/继续，并显示居中的短暂播放状态动画。
- 时间轴悬停气泡始终跟随真实光标，拖拽释放后提交固定时间戳；直连源忽略 Seek 时自动回退到 Emby `StartTimeTicks` 流。
- 放大时间轴圆点并增加白色描边，重做 ±10 秒与音量控件。
- 常驻网络信息只显示真实接收速率，缓存/带宽比降级到诊断 Tooltip。
- PlayerWindow 使用 MainWindow Owner 关系，关闭或返回后自动激活原详情/媒体库窗口。


这一版把“切页面就等接口”的模型改成缓存优先：

- 新增服务器+用户隔离的元数据缓存：强类型内存热缓存 + 96 MiB 有界磁盘缓存。
- stale-while-revalidate：新鲜缓存直接显示；过期但仍可用的数据先显示旧值，同时后台刷新，不让页面卡在 Loading。
- 接入 Emby WebSocket 推送：`LibraryChanged` / `UserDataChanged` 只把相关缓存标成 stale，不删除旧快照；下一次读取仍然瞬间显示旧数据并在后台更新。
- WebSocket 是首选 freshness 信号，较长 TTL 只作为断线/不兼容服务器的兜底，因此正常反复切页不会每几十秒重新请求相同接口。
- 同 endpoint 并发请求自动合并，一个请求在飞时其他页面/预取任务直接复用同一 Task。
- 内存命中直接复用已经反序列化的 C# 对象，不重复 JSON 解析。
- 海报卡鼠标停留约 180 ms 自动预热详情/子目录；快速扫过不会批量打接口。
- 详情页渲染后后台预协商 PlaybackInfo；点击播放时复用仍新鲜的协商结果，不重复 POST。
- Browse 返回栈保存完整列表 + 滚动位置，返回上一层不走 HTTP、甚至不走磁盘缓存。
- Busy 遮罩延迟 180 ms：内存/磁盘命中不会闪一下“正在加载”，真正网络等待才出现。
- 播放结束只把会变化的 Item/Resume 数据标记为 stale，不清空最后快照；海报、媒体库结构等稳定数据继续保持热缓存。
- 剧集列表每次最多向 Emby 拉取 50 条并分页聚合；音量条支持鼠标滚轮调节。
- F12 Network Inspector 在打开时会显示 Metadata Cache 命中，并明确区分客户端计时与服务器字段。

> Tsukimi 的 GPLv3 项目只用于研究公开架构/交互思路；本项目没有复制其 GPL 源码，缓存和预取实现为独立 C# 代码，以维持当前 MIT 许可。

## v0.6.4：实时网速 + 精确拖拽 + 剧集控制

- 播放器底部显示当前视频流的实时接收速度（KB/s / MB/s）、VLC 前向缓存秒数，以及相对当前媒体码率的网络余量。
- 时间轴支持点击任意位置、按住连续拖动、松手提交 Seek；悬停/拖动显示深色目标时间浮层，不再使用白色系统提示框。
- Direct/HLS 自动选择 Seek 方式：可随机定位的直连流走 LibVLC Seek，不可 Seek 的流自动使用带 `StartTimeTicks` 的 Emby 兼容流。
- 剧集信息在后台自动补全，轻量列表缺少 SeriesId / SeasonId 时也会尝试恢复剧集按钮；上一集/下一集移入剧集抽屉，底部主控制区更干净。
- 网络测速和剧集接口失败都属于附加能力，不会中断视频主播放链路。

## v0.6.3：缓冲可视化 + 自适应播放器

### v0.6.3 播放体验

- 播放请求、打开媒体、缓冲、自动兼容重试都有明确的加载动画，不再黑屏假死。
- 缓冲百分比来自 LibVLC `Buffering.Cache`；时间轴的浅色缓冲段只表示播放器当前网络缓存窗口，不伪造整部影片的下载进度。
- VOD 自动使用更稳妥的网络缓存窗口：直连至少 3 秒，HLS/服务端兼容流至少 4.5 秒。
- 外挂字幕在首帧播放后再挂载，字幕服务器异常不会阻塞视频首帧。
- 底部控制区重新压缩为更接近流媒体 App 的图标优先布局。


- 播放主链路与字幕/剧集/用户偏好等补充请求解耦，附加接口变慢不会阻塞首帧。
- 严格以 PlaybackInfo/用户 Policy 判断 DirectPlay、DirectStream、Transcoding，服务器不支持的能力不会显示或被调用。
- 动态音轨、字幕、静音、剧集、上一集/下一集、自动连播与运行时总时长回退。
- LibVLC 原生视频 HWND 下使用系统光标位置兜底唤醒控制层，解决控件隐藏后鼠标移动无法恢复。
- 首页/详情滚轮改为原始 Delta 驱动的阻尼滚动，兼容普通滚轮和高精度滚轮/触控板。
- 列表请求保持轻量 Fields；播放级 MediaSources/MediaStreams 只在真正播放时获取。


Windows 桌面 Emby 客户端，基于 .NET 8、WPF、LibVLCSharp。

## v0.5.0：原生详情导航 + 轻量渲染

这一版把详情页从“模态玻璃浮层”改成主窗口内容区里的真正页面导航。

### 详情页

- 点击电影/单集后，当前窗口内容区立即切换到详情页；左上角返回，不创建新窗口、不加整页遮罩、不锁住主页面。
- 返回时直接恢复原 Home / Browse 页面和滚动状态，不重新请求列表。
- Hero 支持 Item Backdrop、Episode ParentBackdrop、Series poster 和 Logo 继承。
- Episode 自动继承 Series 的简介、类型、评分、演职人员等稀疏字段。
- 展示首映日期、评分、时长、清晰度/HDR、类型、播放状态和续播位置。
- 展示全部媒体版本，并随版本切换即时刷新视频、音轨、字幕和文件技术信息。
- 展示章节、当前季剧集、演职人员、相似作品、工作室、制作地区和外部链接。
- “更多信息”继续展示 Item/Server/Parent/Series/Season IDs、入库时间、源尺寸、容器、项目大小/码率、权限、排序信息、Provider IDs、DisplayPreferences、Presentation Key 和 ETag 等接口元数据。
- 技术信息包括分辨率、Codec/Profile/BitDepth、PixelFormat、帧率、HDR/SDR、色彩空间、码率、容器、大小、协议、源类型、默认流 Index、DirectPlay/DirectStream/Transcode 能力、文件名/路径和字幕交付方式。

### 交互与性能

- 删除旧详情页对整个 `AppContentRoot` 动态添加的 `BlurEffect`。
- 删除首页/浏览页每张媒体卡片上的 DropShadowEffect；玻璃层次改用静态半透明边框与渐变。
- 背景光晕由大半径 BlurEffect 改成 RadialGradientBrush，不再实时模糊 600~700px 图层。
- 详情页先立即渲染列表已有数据，再异步补 canonical Item、父 Series、Hero、People、Episodes、Similar。
- Hero 走 Immediate 图片队列；人物、相似作品和剧集缩略图只有接近纵向视口后才开始排队，横向可见项走 Visible、下一小屏走 Prefetch。
- PlaybackInfo 继续保持 lazy：Item 已带 MediaSources 就直接使用，缺失时后台补取。
- Network Inspector 默认只保留轻量 Headers/Timing 元数据；只有打开 F12 调试窗口后，后续请求才抓取并格式化 Response Body，避免正常浏览时把每份 JSON 额外读取/Pretty-print 一遍。
- 进入详情会立即取消 Home/Browse 已不可见的图片等待任务；返回时恢复原页面和滚动位置，只重新调度当前视口图片，不重新请求列表。

### 图片/网络架构

保留 v0.4.x 的 viewport-first 图片系统：

- HTTP/2 优先，可回落 HTTP/1.1；
- Immediate / Visible / Prefetch 三级队列；
- 8 个网络 worker、2 个解码 worker；
- 同图并发请求合并，并支持 Prefetch→Visible/Immediate 优先级提升；
- 最后一个页面消费者离开后，排队/下载中的图片工作会真正取消，不继续浪费带宽；
- 256 / 512 / 1024 / 1600 固定尺寸桶；
- 有界内存 LRU + 磁盘缓存；
- 大列表只创建当前需要的 WPF 卡片。

## 构建

需要 Windows x64 和 .NET 8 SDK。解压后双击：

```text
publish-win-x64.cmd
```

成功后运行：

```text
publish\win-x64\EmbyRemotePlayer.exe
```

必须保留 `publish\win-x64` 目录内的全部 DLL 和 LibVLC 文件。

## 本地数据

```text
%LOCALAPPDATA%\EmbyRemotePlayer
```

服务器访问令牌使用 Windows 凭据管理器保存。

## 快捷键

- 空格：播放/暂停
- 左/右方向键：后退/前进 10 秒
- 上/下方向键：调节音量
- F 或 F11：全屏
- Esc：退出全屏
- F12：Network Inspector
"# EmbyPlayer" 
