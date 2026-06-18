# LiveCaptioner

Desktop WPF application for local live captions from a microphone or Windows system audio.

The current practical target is podcast, meeting, browser, YouTube, and player audio. The project is focused on low-latency local recognition, with Vosk as the preferred engine right now.

## Current Situation

Current project path on the source machine:

```text
C:\Projects\LiveCaptioner
```

Current status:

- The app starts and builds.
- Vosk local recognition is the main workflow.
- Microphone and Windows system audio are supported.
- Russian and English Vosk models are present on the source machine.
- Vosk speaker-vector model has been added and wired into the app.
- Speaker split currently works through:
  - voice vectors from `vosk-model-spk-0.4`, when Vosk returns the `spk` vector;
  - pause-based fallback when no speaker vector is available.
- Speaker separation is experimental. It is not production-grade diarization.
- Logs are written both to a console window and to `Logs`.
- Large model folders and generated logs are ignored by git.

Last verified build command:

```powershell
dotnet build C:\Projects\LiveCaptioner\LiveCaptioner.csproj -p:OutputPath=C:\Users\User\Documents\Codex\2026-06-14\c-projects-test-screenocr\work\LiveCaptionerBuild\
```

Last result:

```text
Build succeeded.
0 warnings.
0 errors.
```

The alternate output path is useful when Visual Studio is running and locks normal `bin` output files.

## Features

- WPF desktop UI on .NET 8.
- Microphone capture.
- Windows system audio capture through WASAPI loopback.
- Live audio level indicator.
- Local Vosk recognition.
- Local Whisper recognition through Whisper.net.
- Windows Speech Recognition fallback.
- Context-sensitive settings for each engine.
- Vosk partial live text.
- Vosk system audio gain.
- Vosk system audio noise gate.
- Vosk sentence formatting.
- Vosk pause-based speaker turns.
- Vosk voice-vector speaker clustering with configurable similarity threshold.
- Always-on-top window.
- Save transcript to `.txt`.
- Runtime logging to console and log file.

## Recognition Engines

### Vosk local

Current preferred mode.

It gives the best responsiveness for live captions. The service converts incoming audio to `16 kHz mono PCM`, queues small PCM chunks, and feeds them to `VoskRecognizer` on a worker task so audio callbacks do not freeze the UI.

Vosk can use:

- microphone input;
- Windows system audio;
- partial live text;
- optional interview vocabulary;
- optional speaker-vector model.

### Whisper balanced

Uses local Whisper with more context from previous text. Better stability, higher latency.

### Whisper fast / raw

Uses local Whisper with less context. Lower latency than balanced mode, but less stable.

### Windows Speech Recognition

Uses the built-in Windows recognizer. No external model file is needed, but quality is usually weaker for podcasts and system audio.

## Models

Models are stored in:

```text
C:\Projects\LiveCaptioner\Models
```

This directory is intentionally ignored by git.

Known model folders/files on the source machine:

```text
ggml-base.bin
vosk-model-en-us-0.22
vosk-model-en-us-0.42-gigaspeech
vosk-model-ru-0.10
vosk-model-spk-0.4
```

Vosk model selection accepts original Vosk folder names. For example:

```text
vosk-model-en-us-0.22
vosk-model-en-us-0.42-gigaspeech
vosk-model-ru-0.10
```

The speaker model path expected by the app:

```text
C:\Projects\LiveCaptioner\Models\vosk-model-spk-0.4
```

Vosk models can be downloaded from:

```text
https://alphacephei.com/vosk/models
```

Whisper expected files:

```text
C:\Projects\LiveCaptioner\Models\ggml-base.bin
C:\Projects\LiveCaptioner\Models\ggml-small.bin
```

The app can download missing Whisper models from the UI.

## Recommended Vosk Settings

For Russian podcast/audio:

- `Source`: `Microphone` or `Windows system audio`
- `Language`: `ru`
- `Recognition engine`: `Vosk local`
- `Fast live text`: enabled
- `Sentence formatting`: enabled
- `Split roles by pauses`: enabled if no reliable speaker vectors are available
- `Distinguish roles by voice`: enabled when `vosk-model-spk-0.4` is present
- `Voice similarity threshold`: start around `0.82`

Speaker threshold tuning:

- If different speakers are merged into `Speaker 1`, raise the threshold, for example `0.86` to `0.90`.
- If one real speaker is split into many speakers, lower the threshold, for example `0.75` to `0.80`.

For Windows system audio:

- Start with gain around `1.60x`.
- Keep the noise gate enabled if background noise causes false recognition.
- Make sure the audio is playing through the default Windows output device.

## Speaker Separation

The current speaker separation is heuristic.

There are two mechanisms:

- Voice-vector clustering from Vosk `spk` vectors.
- Pause-based speaker turns as a fallback.

Important limitations:

- Vosk does not do full diarization by itself.
- The app clusters `spk` vectors locally using cosine similarity.
- If Vosk does not return an `spk` vector in final JSON results, the app cannot separate by voice and will log that fact.
- Podcast audio with music, compression, overlapping voices, or very short turns can still be difficult.

Useful log messages:

```text
Vosk speaker model enabled.
Vosk final text length=..., speakerVector=...
Speaker vector matched Speaker N, score=...
Speaker vector created Speaker N, bestScore=...
Vosk speaker model is enabled but final result has no spk vector.
```

## Logs

Logs are written to:

```text
C:\Projects\LiveCaptioner\Logs
```

The app also opens a console window and writes runtime diagnostics there.

Logs include:

- app startup and shutdown;
- selected engine, language, source, model;
- Vosk model load;
- Vosk speaker model load;
- memory checkpoints;
- PCM queue backlog warnings;
- recognition errors;
- speaker-vector diagnostics.

## Git Ignore

The project ignores local models and generated logs:

```text
/Models/
/Logs/
*.ggml
ggml-*.bin
vosk-model*/
```

Do not commit model folders or log files.

## Build And Run

Restore:

```powershell
dotnet restore C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

Build:

```powershell
dotnet build C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

Build to alternate output when Visual Studio locks `bin`:

```powershell
dotnet build C:\Projects\LiveCaptioner\LiveCaptioner.csproj -p:OutputPath=C:\Users\User\Documents\Codex\LiveCaptionerBuild\
```

Run:

```powershell
dotnet run --project C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

## Agent Handoff Context

Use this section when continuing the work on another computer or in another agent session.

Project intent:

- Build a local desktop live caption app similar in spirit to Windows captions, Zoom captions, or Teams captions.
- The user wants practical captions for podcasts, meetings, YouTube, browser audio, and interviews.
- Keep the workflow local where possible.
- Avoid external services, tokens, Python pipelines, or cloud APIs unless the user explicitly asks.
- Low latency and usable text are more important than academic-perfect transcription.

Important current files:

```text
MainWindow.xaml
MainWindow.xaml.cs
Services\Audio\Pcm16kMonoConverter.cs
Services\Diagnostics\AppLogger.cs
Services\Speech\VoskSpeechRecognitionService.cs
Services\Speech\VoskRecognitionResult.cs
Services\Speech\VoskRecognitionOptions.cs
Services\Speech\VoskAudioSource.cs
Services\Speech\WhisperModelManager.cs
Services\Speech\WindowsSpeechRecognitionService.cs
```

Important implementation notes:

- `MainWindow.xaml.cs` still orchestrates most UI and engine switching.
- `VoskSpeechRecognitionService` loads the Vosk model and optional speaker model.
- Vosk final recognition emits `VoskRecognitionResult`, which contains text and optional `SpeakerVector`.
- Speaker clustering is currently in `MainWindow.xaml.cs`.
- The speaker clustering threshold is controlled by `VoskSpeakerVectorThresholdSlider`.
- Partial text updates the current caption block.
- Final text commits into the active caption block.
- When voice-vector speaker changes, the UI starts a new caption paragraph.
- Pause-based speaker turns are still used as fallback.

Known rough edges:

- Speaker diarization is approximate.
- Vosk speaker vectors may not always be emitted.
- UI text has Russian labels; preserve UTF-8 when editing.
- `LiveCaptioner.slnx` may be untracked depending on the machine.
- Visual Studio may lock build outputs while the app is running.

Do not reintroduce unless explicitly requested:

- Pyannote/HuggingFace diarization.
- Python-based delayed diarization pipeline.
- External cloud recognition.

Good next steps:

- Improve speaker clustering diagnostics in the UI.
- Add a compact speaker debug panel showing vector/match score.
- Add named speakers and manual speaker correction.
- Add transcript export with timestamps and speaker labels.
- Move speaker clustering out of `MainWindow.xaml.cs` into a service.
- Add presets for podcast, meeting, YouTube, and interview.

## Notes

- System audio capture depends on the default Windows playback device.
- Vosk system audio and microphone audio are converted internally to `16 kHz mono PCM`.
- Whisper uses WAV chunks and normally has higher latency.
- When testing speaker separation, use clear alternating speakers and at least a few seconds of speech per speaker.
