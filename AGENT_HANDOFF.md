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
- Current best mode is `Vosk local` with system audio.

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
  - simple pause-based speaker turns
- Windows Speech Recognition engine.
- Always-on-top checkbox.
- Save transcript to `.txt`.

## Important Current UI Settings

Recommended Vosk system-audio test settings:

- `Источник звука`: `Системный звук Windows`
- `Язык`: `en`
- `Движок распознавания`: `Vosk local`
- `Vosk fast live text`: enabled
- `Vosk interview vocabulary`: enabled
- `Vosk system audio gain`: start around `1.60x`
- `Vosk system noise gate`: enabled
- `Vosk auto speaker turns`: optional
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
- `Services/Speech/WhisperModelManager.cs`
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
- Pause-based speaker turns can create a new block and alternate `Speaker 1` / `Speaker 2`.

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
  - Tried rough speaker separation by local pitch/timbre features.
  - Not useful enough.
  - User asked to roll it back.

Do not reintroduce these unless the user explicitly asks.

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
- Avoid external services/tokens/APIs for core workflow.
- Keep the app local where possible.
- Do not pursue speaker diarization if it adds large delays or setup complexity.

## Good Next Steps

Potential improvements that fit the current direction:

- Improve Vosk sentence formatting.
- Add configurable pause threshold for `Vosk auto speaker turns`.
- Add a clear transcript export format.
- Add user-editable interview vocabulary file.
- Add profiles/presets for:
  - interview
  - YouTube/browser
  - meeting/system audio
- Improve UI text encoding if Russian labels appear garbled on another machine.

