# LiveCaptioner Agent Handoff

Use this file to quickly restore project context in another Codex/agent session.

## Project

Path:

```text
C:\Projects\LiveCaptioner
```

App type:

- WPF desktop app.
- .NET 8.
- Local live captions from microphone or Windows system audio.

Primary goal:

- Fast, readable live captions for meetings/interviews/browser/system audio.
- User prefers low latency over perfect recognition.
- Current best cloud mode is `OpenAI cloud - fast` with Windows system audio and language `en`.
- Current best local/offline mode is `Vosk local` with system audio.
- `OpenAI cloud - fast` uses OpenAI Realtime WebSocket transcription and is much lower latency than chunked OpenAI transcription.

## Current Build State

Last verified command:

```powershell
dotnet build C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

Result:

- Build succeeds.
- 0 warnings.
- 0 errors.

## Current NuGet Packages

From `LiveCaptioner.csproj`:

- `NAudio` `2.3.0`
- `System.Speech` `10.0.9`
- `Vosk` `0.3.38`
- `Whisper.net` `1.9.1`
- `Whisper.net.Runtime` `1.9.1`

No OpenAI SDK package is used. OpenAI chunked transcription is implemented with `HttpClient`; OpenAI realtime transcription is implemented with `ClientWebSocket`.
No Sherpa-ONNX NuGet package was available as `SherpaOnnx`.

## Current Models

Models folder:

```text
C:\Projects\LiveCaptioner\Models
```

Known local models on the source machine:

```text
ggml-base.bin
ggml-small.bin
vosk-model-en-us-0.22
```

Whisper expected files:

```text
C:\Projects\LiveCaptioner\Models\ggml-base.bin
C:\Projects\LiveCaptioner\Models\ggml-small.bin
```

Vosk supports both short and original folder names:

```text
C:\Projects\LiveCaptioner\Models\vosk-model-en
C:\Projects\LiveCaptioner\Models\vosk-model-en-us-0.22
```

Vosk download page:

```text
https://alphacephei.com/vosk/models
```

Recommended English Vosk model:

```text
vosk-model-en-us-0.22
```

## Current Features

Implemented and kept:

- Microphone capture.
- Windows system audio capture via WASAPI loopback.
- Audio level indicator.
- Whisper recognition:
  - `Whisper balanced`
  - `Whisper fast / raw`
- Vosk recognition:
  - microphone
  - system audio
  - original Vosk model folder-name detection
  - partial live text
  - interview vocabulary mode
  - system audio gain
  - system audio noise gate
  - sentence formatting
  - speaker split by Vosk `spk` voice vectors when `vosk-model-spk-0.4` is available
  - optional new paragraph after long silence, without changing the speaker label
- Windows Speech Recognition engine.
- OpenAI cloud engine:
  - `OpenAI cloud - fast`: realtime WebSocket session using `gpt-realtime-2` with input transcription model `gpt-realtime-whisper`
  - `OpenAI diarize - slower`: chunked `gpt-4o-transcribe-diarize`
  - optional experimental local speaker split in realtime mode through pitch/timbre voiceprints
  - requires `OPENAI_API_KEY`
- Sherpa-ONNX engine is selectable but currently waits for a native/helper bridge at `Tools\sherpa-onnx\sherpa-onnx.exe`.
- Always-on-top checkbox.
- Save transcript to `.txt`.

## Important Current UI Settings

Recommended OpenAI realtime system-audio test settings:

- `Profile`: `Podcast / Video`
- `Source`: `Windows system audio`
- `Language`: `en`
- `Recognition engine`: `OpenAI cloud - fast`
- `Split speakers locally by voice`: enabled only when testing experimental speaker split
- Use `OpenAI cloud - fast` for low-latency English monologues and videos.
- Use `OpenAI diarize - slower` only when official chunked speaker labels are more important than latency.

Recommended Vosk system-audio test settings:

- `Источник звука`: `Системный звук Windows`
- `Язык`: `en`
- `Движок распознавания`: `Vosk local`
- `Vosk fast live text`: enabled
- `Vosk interview vocabulary`: enabled
- `Vosk system audio gain`: start around `1.60x`
- `Vosk system noise gate`: enabled
- `Split speakers by voice`: enabled when the Vosk speaker model is available
- `New paragraph after long silence`: optional; do not use pauses to invent speakers
- `Vosk sentence formatting`: enabled

Recommended Whisper test settings:

- `Движок распознавания`: `Whisper balanced`
- `Модель Whisper`: `base` for speed, `small` for accuracy
- `Длина аудио-куска`: `3 сек.`

## Architecture Notes

Current service structure:

- `Services/Audio/IAudioCapture.cs`
- `Services/Audio/MicrophoneAudioCapture.cs`
- `Services/Audio/SystemAudioCapture.cs`
- `Services/Audio/AudioMath.cs`
- `Services/Audio/Pcm16kMonoConverter.cs`
- `Services/Audio/PcmMonoConverter.cs`
- `Services/Audio/RealtimePcmAudioCapture.cs`
- `Services/Speech/WhisperModelManager.cs`
- `Services/Speech/LocalSpeakerDiarizer.cs`
- `Services/Speech/OpenAIRealtimeTranscriptionService.cs`
- `Services/Speech/OpenAITranscriptionService.cs`
- `Services/Speech/OpenAITranscriptionResult.cs`
- `Services/Speech/SherpaOnnxSpeechRecognitionService.cs`
- `Services/Speech/WindowsSpeechRecognitionService.cs`
- `Services/Speech/VoskSpeechRecognitionService.cs`
- `Services/Speech/VoskRecognitionOptions.cs`
- `Services/Speech/VoskAudioSource.cs`
- `Models/AudioChunk.cs`

`MainWindow.xaml.cs` still orchestrates the UI and engine switching.

## Vosk Implementation Notes

`VoskSpeechRecognitionService`:

- Opens microphone through `WaveInEvent`.
- Opens system audio through `WasapiLoopbackCapture`.
- Converts system audio to `16 kHz mono PCM` using `Pcm16kMonoConverter`.
- Feeds PCM directly to `VoskRecognizer`.
- Emits:
  - final text through `TextRecognized`
  - partial text through `PartialTextRecognized`
  - level through `AudioLevelChanged`

Partial text updates the current caption line instead of appending repeated chunks.

## Caption Behavior

Current Vosk caption behavior:

- A single active paragraph grows while speech continues.
- Partial Vosk text updates the active line.
- Final Vosk text is appended to the active paragraph.
- Sentence formatting capitalizes and adds basic punctuation.
- Pause-based speaker changes were removed. Long silence can optionally start a new paragraph, but never changes `Speaker N`.

## OpenAI Implementation Notes

`OpenAIRealtimeTranscriptionService`:

- Used by `OpenAI cloud - fast`.
- Opens `wss://api.openai.com/v1/realtime?model=gpt-realtime-2`.
- Sends `session.update` with:
  - `session.type = realtime`
  - `session.output_modalities = ["text"]`
  - `audio.input.format = { type = audio/pcm, rate = 24000 }`
  - `audio.input.transcription.model = gpt-realtime-whisper`
  - language from the UI, usually `en`
- Accepts `conversation.item.input_audio_transcription.delta` and `conversation.item.input_audio_transcription.completed`.
- `RealtimePcmAudioCapture` sends `24 kHz mono PCM16` chunks from microphone or WASAPI loopback system audio.
- This mode is the current preferred OpenAI mode for low-latency captions.

`LocalSpeakerDiarizer`:

- Used only by OpenAI realtime when `Split speakers locally by voice` is enabled.
- It is experimental and local; OpenAI realtime is not returning speaker labels here.
- It estimates voiceprints from pitch, zero-crossing rate, voice-band energies, and spectral tilt/band-ratio features.
- It avoids pause-based speaker switching.
- It requires stable voiceprint evidence before creating or switching speakers.
- It can distinguish male/female voices better than similar male/male voices.
- When a late speaker switch is confirmed, `MainWindow.xaml.cs` trims the old active partial caption and moves current partial text to the new speaker block.

`OpenAITranscriptionService`:

- Uses `HttpClient`.
- Reads `OPENAI_API_KEY` from the environment.
- Calls `https://api.openai.com/v1/audio/transcriptions`.
- Uses `response_format=json` for `gpt-4o-mini-transcribe` if chunked non-diarized fallback is used.
- Uses `response_format=diarized_json` and `chunking_strategy=auto` for `gpt-4o-transcribe-diarize`.
- `OpenAI diarize - slower` is chunk-based, so speaker IDs may not stay perfectly stable across chunks and latency is higher.

## Sherpa-ONNX Implementation Notes

`SherpaOnnxSpeechRecognitionService` is a boundary/stub.

- Expected runtime bridge: `Tools\sherpa-onnx\sherpa-onnx.exe`.
- Do not fake recognition through Vosk here.
- Next useful step is adding a native/C# bridge or helper process based on official sherpa-onnx C#/C++ examples and local ONNX models.

Important limitation:

- This is not real diarization.
- It does not identify actual people by voice.

## Things Tried And Reverted

These were intentionally removed:

- `Pyannote delayed`
  - Required Python/HuggingFace/token/model setup.
  - Introduced unwanted delay.
  - User explicitly rejected this direction.
- `Local voice heuristic`
  - Earlier rough pitch/timbre attempt was not useful enough.
  - Current version has been reintroduced as `LocalSpeakerDiarizer` for OpenAI realtime and is still experimental.
  - Do not replace the realtime transcription path with slow chunked diarization unless the user explicitly accepts the latency tradeoff.

Do not reintroduce Pyannote/Python delayed diarization unless the user explicitly asks.

## Current README

`README.md` was rewritten to match the current state.

It includes:

- Features.
- Requirements.
- NuGet package list.
- Model locations.
- Vosk model download guidance.
- Recommended settings.
- Notes about speaker turns and limitations.

## User Preferences For This Project

- Russian conversation is fine.
- The app is for practical interview/meeting support.
- User prefers low latency and usable text over perfect academic diarization.
- OpenAI API is now an accepted workflow for cloud mode, but local/offline options should remain.
- Keep the app local where possible, but do not remove the working OpenAI realtime path.
- Avoid speaker diarization approaches that add large delays or setup complexity.

## Good Next Steps

Potential improvements that fit the current direction:

- Improve Vosk sentence formatting.
- Add speaker-vector diagnostics: last `spk` availability, best match score, selected speaker.
- Add manual speaker names/correction.
- Add a clear transcript export format.
- Add user-editable interview vocabulary file.
- Add profiles/presets for:
  - interview
  - YouTube/browser
  - meeting/system audio
- Improve UI text encoding if Russian labels appear garbled on another machine.

