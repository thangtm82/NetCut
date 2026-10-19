# NetCut — Native .NET MAUI Video Editor

A **100% C# / XAML user interface** for a Windows video editor inspired by Donkey Cut. This is a new native implementation, **not** a React/WebView wrapper or a completed 1:1 port.

## First milestone

- Native .NET MAUI XAML shell (Windows only, .NET 10)
- Media import and FFprobe duration detection
- Native project and clip data models
- JSON save/open
- Clip-list timeline with basic In/Out trimming
- FFmpeg MP4 export for a **single** clip

**Not implemented yet:** native video playback/preview, multitrack timeline, transitions, compositing, multi-clip rendering, audio mixing, subtitles UI/transcription, AI chat, TTS, project migration, and full Donkey Cut compatibility.

## Prerequisites

Windows 10/11, .NET 10 SDK and MAUI workload, FFmpeg and FFprobe on PATH. Optionally set `NETCUT_FFMPEG` and `NETCUT_FFPROBE` to full executable paths.

## Build

```powershell
dotnet workload install maui
dotnet build .\NetCut\NetCut.csproj -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=None
```

## Roadmap

1. Native WinUI MediaPlayerElement playback and seek.
2. Track-based editor with drag/drop, split, ripple, undo/redo.
3. FFmpeg multi-clip exports, subtitle filters, compositing and mixing.
4. Local faster-whisper, SRT import/export.
5. AI chat/TTS/music providers and packaging.

## License consideration

This first milestone uses original C# and XAML source. Review all third-party licenses before porting protected assets or bundling FFmpeg and model runtimes.


## Phase 3 (experimental): timeline editing and sequential export

The phase 3 pull request adds a native C# timeline operations service
(`Split`, `Trim`, `Move`, `Snap`) and a **sequential V1-only**
multi-clip MP4 export. Select a clip and click **Split Selected Clip**;
the prompt accepts an absolute timeline time in seconds.

**Exporter limitations**: V1 clips must begin at 0 and follow end-to-end
without gaps or overlaps (tolerance 0.04s). Multiple video layers,
overlays, audio tracks, effects and transitions are **rejected**, not
rendered. Each source is normalized to 1280x720 at 30fps with AAC
stereo before concatenation; audio-less inputs receive silence.
The exported video is written under the MAUI application data directory.

Requires FFmpeg and FFprobe installed. Test Windows compilation and a
small multi-clip export before merging or distributing this code.


## Experimental native multitrack FFmpeg compositor (PR phase 4)

`CompositionPlan` builds a `filter_complex` graph of a black 720p canvas,
overlaying video clips in ascending `TrackIndex` (higher tracks appear above lower)
and mixing audio streams from all timeline clips. Absolute `StartSeconds`,
source In/Out trimming, audio-only inputs, and gaps are supported in the export
plan. The editor uses `FfmpegService.ExportCompositionAsync` for its MP4 export.

**Known limitations:** no live composited preview, fades, transitions,
opacity, clip gain, waveform, seek synchronization, cancel/progress UI, hardware
acceleration, or robust render presets. All source audio streams are mixed at
unity gain and might clip; output is fixed at 1280x720, 30fps, H.264/AAC.
Overlapping clips on the same track have deterministic sequential ordering,
not a fully-defined video-editor z-order. Large projects may be slow.
This feature has not yet passed a Windows build or end-to-end video test.


## Phase 8: cached continuous timeline preview (experimental)

The **Play Full Timeline (Cached)** button exports a temporary complete composite
as an MP4 and plays it through the native WinUI media player. A SHA-256 cache
key includes clip timing, track order, and source file size/modification time;
repeated plays reuse the same rendered MP4. The Stop Preview button pauses
playback and cancels an in-flight render.

**Current limitations:** first playback requires rendering the entire timeline;
there is no chunked background prefetch or partial cache invalidation. Native
transport controls can seek within the rendered MP4, but the MAUI playhead
slider is not continuously updated from actual playback position. Media changes
not reflected by file size/mtime may evade invalidation. Cache eviction is not
automatic. Audio/video sync depends on experimental FFmpeg compositor output.
Windows runtime validation is still needed.


### Phase 8 chunk preview (experimental)

Use **Play Cached Chunks** to start a five-second timeline-chunk playback
at the slider position. Each chunk has a content-addressed cache identity
computed from intersecting clips and source file modification metadata.
The next chunk is prefetched before playback reaches the boundary.
WinUI PlaybackSession reports the current position to the MAUI slider;
the next chunk starts when MediaEnded fires. Preview cache uses an
approximate 2 GiB least-recently-used cleanup policy. Entirely empty
intervals render black and silent, and trailing gaps are preserved.

**Caveats:** Chunk transitions can stutter if FFmpeg is still rendering;
there is no seamless buffered playlist or GPU-accelerated live compositor.
Audio continuity at segment boundaries, caching under concurrent edits,
player seek/position behavior and background task cancellation require
Windows runtime testing. A successful CI build alone does not guarantee
accurate playback. The old full-timeline cache mode remains available.
