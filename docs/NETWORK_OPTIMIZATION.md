# v0.4.0 Network and image-loading strategy

## The old failure mode

The old implementation created one Task for every card and used `Task.WhenAll`. Even with a semaphore, an entire page was queued immediately. A Home page could therefore schedule dozens of images before the user ever scrolled to them, while a Favorites/Search page could schedule hundreds.

Increasing the semaphore from 4 to 8 would only make this fan-out faster; it would not fix the architecture.

## New request policy

### Metadata

- Latest / Resume / Library / Favorites / Search use lightweight card fields.
- `MediaSources` and `MediaStreams` are delayed until detail/playback.
- Home sections still load independently.
- Latest Movies / Episodes keep a 5-minute memory cache.
- Clicking Home respects that cache rather than forcing a refresh every time.
- Player close refreshes only Resume.

### Artwork

- no full-page `Task.WhenAll`;
- only visible items are high priority;
- only one short next-screen range is prefetched;
- fixed widths: 256 / 512 / 1024 / 1600;
- card artwork uses server-side resizing and quality 82;
- duplicate requests share the same in-flight Task;
- global worker count: 8;
- decode concurrency: 2;
- memory LRU budget: ~220 MiB;
- disk budget: ~768 MiB.

### HTTP

- HTTP/2 preferred;
- HTTP/1.1 fallback automatic;
- pooled connections retained;
- max connections per server 16;
- transient GET retry policy remains enabled.

## Expected behavior

On a cold cache, the first visible artwork should begin appearing as soon as its metadata section arrives. Images below the current viewport no longer compete equally with the first screen. On a warm cache, fixed width buckets and disk reuse eliminate most network requests.

Actual latency still depends on the Emby server, reverse proxy/CDN, client route, TLS handshake, and whether the server/CDN serves artwork from cache. The client architecture is designed so those external delays are not multiplied by unnecessary requests.
