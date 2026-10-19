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
