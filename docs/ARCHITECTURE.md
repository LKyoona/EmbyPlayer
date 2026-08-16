# Architecture

## UI layer

`MainWindow` owns navigation and orchestration. `MediaCardViewModel` owns card presentation plus a tiny image-load state flag used to prevent duplicate viewport scheduling. `PlayerWindow` owns LibVLC lifecycle and playback controls.

The UI no longer treats “page data returned” as “download every image now”. Metadata and image delivery are separate pipelines.

## Service layer

- `EmbyApiClient`: authentication, lightweight media queries, playback negotiation, stream URLs, progress reports, HTTP/2-preferred transport.
- `ImageCacheService`: viewport-priority scheduler, request coalescing, fixed image-size buckets, bounded network/decode concurrency, bounded LRU memory cache, bounded disk cache.
- `ProfileStore`: atomic JSON settings and Windows Credential Manager tokens.
- `LogService`: daily local log files that never throw back into the application.

## Image pipeline

### 1. Metadata first

List endpoints return only data needed to construct cards. `MediaSources` / `MediaStreams` remain excluded from list requests.

### 2. Viewport planner

`MainWindow` calculates which cards are visible from scroll offset and viewport size. It schedules:

1. current visible cards as `Visible`;
2. a short next-screen window as `Prefetch`;
3. detail hero images as `Immediate`.

A 100-item page therefore does not produce 100 immediate image requests.

### 3. Size normalization

All requested widths map to one of four buckets:

- 256px
- 512px
- 1024px
- 1600px

This means a poster requested by Home and then Search reuses the same cached representation instead of creating many near-duplicate 220/300/360px files.

### 4. Request coalescing

The cache key contains server + item + image type + Emby image tag + normalized width. A `ConcurrentDictionary<string, ImageRequestEntry>` guarantees one shared in-flight request for a key. Requests keep a consumer count, can be promoted from Prefetch to Visible/Immediate, and are canceled when their last page consumer disappears.

### 5. Priority workers

Eight global image workers service three queues. Each time a worker becomes free it checks Immediate, then Visible, then Prefetch. Prefetch can never grow into an unbounded per-page fan-out because only a short look-ahead range is scheduled.

### 6. Decode isolation

Image download concurrency and WPF/WIC decode concurrency are separate. Only two images decode simultaneously. `DecodePixelWidth` is always set before bitmap creation.

### 7. Bounded caches

- memory: LRU, approximately 220 MiB decoded-image budget;
- disk: approximately 768 MiB, oldest files removed in background;
- Emby image tags make changed artwork naturally use a new cache key.

## Large browse lists

Browse pages keep the complete metadata result but only insert the first 36 cards into the WPF visual tree. When the user approaches the bottom, 24 more cards are appended. This reduces initial layout cost and keeps image scheduling aligned with real navigation.

## HTTP strategy

`HttpClient` requests HTTP/2 by default with `RequestVersionOrLower`, so incompatible servers transparently fall back to HTTP/1.1. Connection pooling is retained across metadata and artwork requests. Application-level image workers remain the authority on concurrency instead of relying on arbitrary `Task.WhenAll` fan-out.


## Diagnostics cost isolation

Network Inspector always keeps lightweight request metadata (URL, headers, status, timing and content length), but response/request bodies are captured only while the F12 inspector window is open. This keeps diagnostics available without forcing normal navigation to buffer, decode, pretty-print and redact every JSON payload twice.

## Playback state flow

1. Open details with the normal Item endpoint and reuse its `MediaSources` when available.
2. Only when the user presses Play, negotiate PlaybackInfo using the current resume position.
3. Select the requested media source and preserve server-signed absolute direct URLs.
4. Build a direct URL and a transcode fallback URL.
5. Attach the default external subtitle URL when Emby exposes one.
6. Start direct playback when available.
7. If no `Playing` event arrives within 16 seconds, or LibVLC raises an error, switch once to HLS transcoding.
8. Report start/progress/stop events using Emby stream indexes.
9. For seeking during transcoding, restart the HLS session with a new `StartTimeTicks` value.

## Failure isolation

- navigation requests use cancellation tokens so stale results cannot overwrite newer pages;
- viewport image sessions have their own cancellation lifetime; entering detail intentionally cancels off-screen Home/Browse image consumers so they stop competing for bandwidth, and Back starts a fresh viewport session without reloading list metadata;
- GET requests retry transient network and 5xx responses;
- image failures stay local to a card and can retry when re-entering the viewport;
- playback reporting failures never interrupt playback;
- configuration writes use a temporary file followed by atomic replacement;
- fatal UI errors are logged before shutdown.

## v0.5 detail-page architecture

The detail screen is a sibling page of the Home/Browse page inside the main window's content column. Only one is visible at a time. There is no modal overlay and no whole-window blur.

Detail loading is progressive:

1. swap pages and render the clicked card's existing metadata immediately;
2. request the canonical Item and start a high-priority hero image in parallel;
3. apply MediaSources immediately when the Item already contains them;
4. fetch/cache Series metadata for Episode pages so sparse episode fields inherit complete series presentation data;
5. independently populate hero/logo, cast, current-season episodes and Similar Items;
6. create cast/episode/similar metadata independently, but do not request their artwork until each section approaches the vertical viewport; visible horizontal items use Visible and only a short look-ahead uses Prefetch;
7. fetch PlaybackInfo lazily only when MediaSources are absent or playback begins.

Image requests also propagate page cancellation into queued/in-flight work, so stale navigation no longer keeps downloading invisible artwork. Repeated card shadows and whole-page blur are deliberately avoided. Static gradients, semi-transparent borders and pre-sized decoded images provide the visual depth while keeping WPF's visual tree and composition cost predictable.


## v0.7 cache-first metadata pipeline

Metadata navigation now follows a cache-first stale-while-revalidate pipeline:

1. `EmbyApiClient` constructs a canonical endpoint key scoped to server + user.
2. `ApiResponseCacheService` checks the typed in-memory cache. A memory hit returns the existing C# object without JSON parsing.
3. If memory misses, a small persistent binary metadata cache is checked. The file header stores the response timestamp separately from filesystem mtime; mtime is used only for disk-LRU cleanup.
4. Fresh entries return immediately with no network access.
5. Stale-but-acceptable entries return immediately and start one background refresh.
6. Missing/expired entries use request coalescing: all consumers await the same in-flight Task for that endpoint.
7. Stable metadata and image caches are not flushed after playback. Only progress-derived Item/Resume endpoints are invalidated.

UI orchestration adds another layer above the service cache: Browse navigation stores the actual result list and scroll offset in a C# in-memory snapshot stack, so Back is synchronous from the user's perspective. Card hover uses a short dwell before prefetching detail/children, and detail playback negotiation is warmed after the page has rendered.

The blocking busy overlay is deliberately delayed by 180 ms. A cache hit should look like normal navigation, not like a network request that happened to finish quickly.

Tsukimi was reviewed as an external architecture reference, particularly its release-note emphasis on non-blocking player work, buffer-aware seeking, compact controls, hardware decode handling, and bounded episode loading. Tsukimi is GPL-3.0, so its source is not copied into this MIT codebase; these mechanisms are independent WPF/C# implementations.


## v0.7 push-driven cache invalidation

Emby websocket notifications are treated as freshness hints, never as a hard dependency. After authentication the client opens a `ClientWebSocket` using the same server address with `ws/wss` and the current token/device id. The connection reconnects with bounded exponential backoff; playback and navigation continue normally when it is unavailable.

`LibraryChanged` marks the complete metadata scope stale but preserves every in-memory/disk snapshot. `UserDataChanged` marks Resume/favorites and the specific changed Item records stale. A stale marker does not remove data: the next caller receives the existing typed C# object immediately, and request coalescing performs one background revalidation. This preserves the key invariant that a server-side change may cause network work, but never turns an already-renderable page back into a blocking loading screen.

Normal freshness windows are intentionally longer once push invalidation is available. They are only a safety net for servers/proxies that do not support websocket delivery. Image caching remains tag-keyed and independent, while video buffering remains owned by LibVLC rather than being confused with metadata caching.
