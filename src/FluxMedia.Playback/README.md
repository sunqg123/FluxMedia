# FluxMedia.Playback

Single-assembly player package for FluxMedia.

## Quick Start

```csharp
using FluxMedia.Playback;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
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

## Playback Speed

Use `Player.CanChangeSpeed` to enable speed controls. `Player.Speed` supports binding,
and `SetSpeedAsync` accepts rates in the configured inclusive range, defaulting to 0.1–10.
Configure `MinPlaybackRate` and `MaxPlaybackRate` on `FluxEngineOptions`; all players share this startup policy.
The bounds must be positive finite values whose range includes 1.
`MaxAudioTempoStages` is calculated automatically and is read-only. The larger stage count needed by either bound is used:
0.1–10 needs four stages, 0.1–30 needs five, and 0.1–40 needs six. A 1–1 range needs no tempo filter.
Counting and filter planning share the same splitting rule; applications do not configure an audio-filter budget.
The resource ceiling is 16 stages. `Start` rejects ranges requiring more stages, reporting the bounds, required count, and ceiling before creating filters.
Reading `MaxAudioTempoStages` with invalid bounds throws an exception identifying the range configuration and offending option.
Invalid configurations throw from `FluxEngine.Start` before logging or native initialization. Player and source options cannot override this policy.
Out-of-range requests fail explicitly instead of being clamped.
Video-only playback uses the same configured range and skips per-request audio planning.
Live and unknown-duration sources reject rate changes in the core.
`Player.DefaultSpeeds` supplies optional UI presets; applications can supply their own lists and rates.
Audio chains FFmpeg atempo filters to preserve pitch, and video follows the audible audio clock across rate changes.
Requests are confirmed after committing the processing chain. Paused/not-started requests return success with `IsDeferred=true`;
observe `IsSpeedChangePending`, `RequestedSpeed`, and `LastSpeedError` for their eventual outcome.
Candidate build failures preserve playback; errors during irreversible commit fail the pipeline.
Only valid new targets supersede pending requests. Cancellation after commit begins waits for the final outcome.
Successful opening does not guarantee a custom initial rate was applied; inspect rate diagnostics and `RateBuildRejected` logs.
Native libraries must include avfilter and the audio filters.
Recording retains the original media timestamps.

Version 1.2.0 tightens the default rate range from arbitrary positive finite values to 0.1–10.
Applications using values outside this range must configure the bounds in `FluxEngineOptions` before starting the engine; audio stage counts follow automatically.

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
