# FluxMedia.Playback

Single-assembly player package for FluxMedia.

## Quick Start

```csharp
using FluxMedia.Playback;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
    ThrowOnMissingFFmpeg = true,
    EnableLogging = false
});

await using var player = FluxEngine.CreatePlayer();

await player.OpenAndPlayAsync("video.mp4");
await player.SeekAsync(TimeSpan.FromSeconds(30));
await player.SetVolumeAsync(0.6);
await player.PauseAsync();
await player.ResumeAsync();
await player.StartRecordingAsync("recording.mp4");
await player.StopRecordingAsync();
await player.StopAsync();
```

## Notes

- The default binding is FFmpeg.AutoGen 9.0.1.1 and requires matching FFmpeg 9 shared libraries.
  Native libraries are supplied by the application.
- Configure FFmpeg through `FluxEngineOptions.FFmpegPath` or
  `FLUXMEDIA_FFMPEG_ROOT`.
- File logging is disabled by default. The global `FluxMedia.Core.Logging.FluxMediaLog`
  supports direct `Info`, `Warning`, `Error`, file output and host callbacks without
  injecting a logger into players. Set `FluxEngineOptions.EnableLogging = true`
  when diagnostic logs are needed.
- Camera and screen recording are not part of this package.

`FFmpegLibraries.RequiredFileNames` lists the files required by the selected
binding. `FFmpegLibraries.IsAvailable(directory)` checks file presence without
loading native code. The binding package version is configured once through
`FFmpegAutoGenVersion` in `Directory.Build.props`.
