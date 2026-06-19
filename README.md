# LiveCaptioner

Desktop WPF application for local live captions from a microphone or Windows system audio.

The current practical target is podcast, meeting, browser, YouTube, and player audio. The project is focused on low-latency practical captions, with OpenAI realtime as the current best cloud mode and Vosk as the current local/offline mode.

## Current Situation

Current project path on the source machine:

```text
C:\Projects\LiveCaptioner
```

Current status:

- The app starts and builds.
- The UI is English-only now to avoid mixed language labels and encoding issues.
- `OpenAI cloud - fast` is the current default workflow for English system-audio monologues and videos.
- `OpenAI cloud - fast` uses the Realtime WebSocket API and streams audio instead of sending delayed WAV chunks.
- OpenAI realtime text works well enough for live captions, but speaker separation is still experimental.
- Vosk local recognition remains the main offline/local workflow.
- Microphone and Windows system audio are supported.
- Russian and English Vosk models are present on the source machine.
- Vosk speaker-vector model has been added and wired into the app.
- Vosk model objects are kept in memory after Stop and reused on the next Start when the selected language/model path did not change.
- The UI uses simple profiles, with detailed Vosk controls hidden under Advanced settings.
- Speaker split currently works through:
  - voice vectors from `vosk-model-spk-0.4`, when Vosk returns the `spk` vector.
  - experimental local voiceprint clustering for OpenAI realtime mode.
- Pauses are no longer used to invent a different speaker because that breaks single-speaker monologues.
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
- OpenAI realtime transcription through `gpt-realtime-2` session + `gpt-realtime-whisper` input transcription.
- Experimental local speaker split for OpenAI realtime using pitch/timbre voiceprints.
- OpenAI chunked cloud transcription through `gpt-4o-mini-transcribe`.
- OpenAI cloud diarization through `gpt-4o-transcribe-diarize`.
- Sherpa-ONNX local engine is selectable, but currently waits for a native runtime bridge under `Tools\sherpa-onnx`.
- Windows Speech Recognition fallback.
- Context-sensitive settings for each engine.
- Vosk partial live text.
- Vosk system audio gain.
- Vosk system audio noise gate.
- Vosk sentence formatting.
- Optional new paragraph after a long silence.
- Vosk voice-vector speaker clustering with configurable similarity threshold.
- Presets:
  - `Meeting`
  - `Podcast / Video`
  - `Interview practice`
  - `Dictation`
  - `Custom`
- Always-on-top window.
- Save transcript to `.txt`.
- Runtime logging to console and log file.

## Recognition Engines

### Vosk local

Current preferred mode.

It gives the best responsiveness for live captions. The service converts incoming audio to `16 kHz mono PCM`, queues small PCM chunks, and feeds them to `VoskRecognizer` on a worker task so audio callbacks do not freeze the UI.

Stop/Start behavior:

- `Stop` releases audio capture, worker task, and the current recognizer.
- Loaded Vosk `Model` and `SpkModel` stay in memory.
- The next `Start` reuses them if the language/model path did not change.
- Models are unloaded on app close or when a different Vosk model path is needed.

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

### OpenAI cloud

There are two OpenAI modes with different tradeoffs.

Supported app modes:

- `OpenAI cloud - fast`: Realtime WebSocket session with `gpt-realtime-2`; input transcription is configured as `gpt-realtime-whisper`.
- `OpenAI diarize - slower`: chunked `/audio/transcriptions` call with `gpt-4o-transcribe-diarize`, `response_format=diarized_json`, and `chunking_strategy=auto`.

Requirements:

```powershell
$env:OPENAI_API_KEY = "your-api-key"
```

Notes:

- `OpenAI cloud - fast` streams `24 kHz mono PCM16` audio to OpenAI, so it has much lower latency than chunked transcription.
- `OpenAI cloud - fast` does not receive speaker labels from OpenAI. The app optionally tries local speaker separation by pitch/timbre voiceprints.
- The UI option `Split speakers locally by voice` controls this experimental local speaker split.
- When local OpenAI speaker split detects a speaker change late, the app now trims the old active caption block back to the last committed text and moves the current partial text into the new speaker block.
- `OpenAI diarize - slower` uses the official diarization transcription model, but it is slower because it sends audio chunks.
- OpenAI modes need internet and API billing.

### Sherpa-ONNX local

This engine is visible in the UI as a future local replacement/companion for Vosk.

Current state:

- The app has a separate Sherpa service boundary.
- NuGet package `SherpaOnnx` was not available.
- The expected bridge path is:

```text
C:\Projects\LiveCaptioner\Tools\sherpa-onnx\sherpa-onnx.exe
```

Until a native bridge/helper is installed there, the UI reports that Sherpa-ONNX is not ready.

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

The UI now exposes these as profiles first. Start with a profile, then open `Advanced Vosk settings` only when tuning is needed.

Recommended profile choices:

- `Meeting`: system audio, noise gate, voice-vector speaker split, moderate speaker threshold.
- `Podcast / Video`: system audio, no noise gate by default, voice-vector speaker split, higher speaker threshold.
- `Interview practice`: microphone, interview vocabulary, speaker split.
- `Dictation`: microphone, speaker split off, stable one-speaker text.

For Russian podcast/audio:

- `Source`: `Microphone` or `Windows system audio`
- `Language`: `ru`
- `Recognition engine`: `Vosk local`
- `Fast live text`: enabled
- `Sentence formatting`: enabled
- `Split speakers by voice`: enabled when `vosk-model-spk-0.4` is present
- `Distinguish roles by voice`: enabled when `vosk-model-spk-0.4` is present
- `New speaker threshold`: start around `0.64` to `0.68`

Speaker threshold tuning:

- If one real speaker is split into many speakers, lower the threshold, for example `0.55` to `0.62`.
- If different speakers are merged into `Speaker 1`, raise the threshold, for example `0.70` to `0.78`.

For Windows system audio:

- Start with gain around `1.60x`.
- Keep the noise gate enabled if background noise causes false recognition.
- Make sure the audio is playing through the default Windows output device.

## Speaker Separation

The current speaker separation is heuristic.

Current mechanism:

- Voice-vector clustering from Vosk `spk` vectors.
- Local OpenAI realtime voiceprint clustering from incoming PCM audio:
  - pitch estimate;
  - zero-crossing rate;
  - energy in voice-oriented frequency bands;
  - spectral tilt and band-ratio features.

Important limitations:

- Vosk does not do full diarization by itself.
- The app clusters `spk` vectors locally using cosine similarity.
- OpenAI realtime does not currently return speaker labels in this app; the local OpenAI speaker split is a best-effort heuristic.
- The OpenAI realtime local splitter is tuned to avoid pause-based speaker changes. It waits for stable voiceprint evidence before switching or creating a speaker.
- Two similar male voices may still be hard to separate. Male/female transitions are easier.
- Because speaker detection lags behind text streaming, some first words of a new speaker may briefly appear in the old caption block. The app now attempts to trim and move the current partial text when a speaker change is confirmed.
- Clustering is intentionally conservative: short phrases and near-current-speaker vectors stay on the current speaker to avoid splitting monologues after pauses.
- If Vosk does not return an `spk` vector in final JSON results, the app cannot separate by voice and will log that fact.
- Pauses can optionally create a new paragraph after long silence, but they do not change the speaker label.
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
Services\Audio\PcmMonoConverter.cs
Services\Audio\RealtimePcmAudioCapture.cs
Services\Speech\LocalSpeakerDiarizer.cs
Services\Speech\OpenAIRealtimeTranscriptionService.cs
Services\Speech\OpenAITranscriptionService.cs
Services\Speech\OpenAITranscriptionResult.cs
Services\Speech\SherpaOnnxSpeechRecognitionService.cs
Services\Speech\VoskSpeechRecognitionService.cs
Services\Speech\VoskRecognitionResult.cs
Services\Speech\VoskRecognitionOptions.cs
Services\Speech\VoskAudioSource.cs
Services\Speech\WhisperModelManager.cs
Services\Speech\WindowsSpeechRecognitionService.cs
```

Important implementation notes:

- `MainWindow.xaml.cs` still orchestrates most UI and engine switching.
- `OpenAIRealtimeTranscriptionService` owns the OpenAI WebSocket connection and realtime transcription session update.
- `RealtimePcmAudioCapture` captures microphone or system audio and emits `24 kHz mono PCM16` for realtime OpenAI.
- `LocalSpeakerDiarizer` is the experimental local voiceprint splitter for OpenAI realtime mode.
- `VoskSpeechRecognitionService` loads the Vosk model and optional speaker model.
- Vosk final recognition emits `VoskRecognitionResult`, which contains text and optional `SpeakerVector`.
- Speaker clustering is currently in `MainWindow.xaml.cs`.
- The speaker clustering threshold is controlled by `VoskSpeakerVectorThresholdSlider`.
- Partial text updates the current caption block.
- Final text commits into the active caption block.
- When voice-vector speaker changes, the UI starts a new caption paragraph.
- Pause-based speaker changes were removed because they split one-speaker monologues incorrectly.

Known rough edges:

- Speaker diarization is approximate.
- OpenAI realtime speaker split is experimental and not official diarization.
- OpenAI realtime speaker split can still merge similar voices or switch late.
- Vosk speaker vectors may not always be emitted.
- UI text is English-only now.
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
