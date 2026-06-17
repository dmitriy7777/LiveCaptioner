# LiveCaptioner

Desktop WPF application for live captions from a microphone or Windows system audio.

The app is intended for meetings, browser audio, YouTube, players, and interview practice. Recognition runs locally.

## Current Features

- Microphone capture.
- Windows system audio capture through WASAPI loopback.
- Live audio level indicator.
- Local Whisper recognition through Whisper.net.
- Local Vosk recognition.
- Switchable recognition engines:
  - `Whisper balanced`
  - `Whisper fast / raw`
  - `Windows Speech Recognition`
  - `Vosk local`
- Whisper model selector:
  - `base`
  - `small`
- Vosk support for original model folder names, for example `vosk-model-en-us-0.22`.
- Vosk fast partial text while speech is still in progress.
- Vosk interview vocabulary mode for common interview/domain words.
- Vosk system audio gain control.
- Vosk system noise gate.
- Vosk sentence formatting.
- Basic Vosk speaker-turn split by pauses for system audio.
- Always-on-top window.
- Save transcript to `.txt`.

## Requirements

- Windows.
- .NET 8 SDK.
- Visual Studio 2022 or `dotnet` CLI.
- For Whisper: local GGML Whisper model files in `Models`.
- For Vosk: local Vosk model folders in `Models`.

No Python runtime, HuggingFace token, or external speaker-diarization service is required in the current version.

## NuGet Packages

The project uses these NuGet packages:

- `NAudio` `2.3.0`
  - Audio capture, microphone input, WASAPI loopback, audio format handling.
- `Whisper.net` `1.9.1`
  - Local Whisper inference.
- `Whisper.net.Runtime` `1.9.1`
  - Native runtime for Whisper.net.
- `Vosk` `0.3.38`
  - Local Vosk speech recognition.
- `System.Speech` `10.0.9`
  - Windows Speech Recognition engine.

Restore packages with:

```powershell
dotnet restore C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

Build with:

```powershell
dotnet build C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

Run with:

```powershell
dotnet run --project C:\Projects\LiveCaptioner\LiveCaptioner.csproj
```

## Model Files

Models are stored in:

```text
C:\Projects\LiveCaptioner\Models
```

The model files are intentionally not part of the project source because they can be large.

### Whisper Models

Expected Whisper files:

```text
C:\Projects\LiveCaptioner\Models\ggml-base.bin
C:\Projects\LiveCaptioner\Models\ggml-small.bin
```

The app can download the selected Whisper model from the UI if it is missing.

Recommended first choice:

- `base` - faster, good for interactive testing.

Use `small` when accuracy matters more than latency.

### Vosk Models

Expected Vosk folders:

```text
C:\Projects\LiveCaptioner\Models\vosk-model-en
C:\Projects\LiveCaptioner\Models\vosk-model-ru
C:\Projects\LiveCaptioner\Models\vosk-model-uk
```

The app also accepts original Vosk folder names. For English, this works:

```text
C:\Projects\LiveCaptioner\Models\vosk-model-en-us-0.22
```

Recommended English model for better quality:

```text
vosk-model-en-us-0.22
```

Smaller and faster alternative:

```text
vosk-model-small-en-us-0.15
```

Download Vosk models from:

```text
https://alphacephei.com/vosk/models
```

## Recommended Settings

For system audio with Vosk:

- `Source`: `Windows system audio`
- `Language`: `en`
- `Recognition engine`: `Vosk local`
- `Vosk fast live text`: enabled
- `Vosk interview vocabulary`: enabled for interview/domain practice
- `Vosk system audio gain`: start with `1.60x`
- `Vosk system noise gate`: enabled
- `Vosk auto speaker turns`: optional
- `Vosk sentence formatting`: enabled

For microphone with Vosk:

- `Source`: `Microphone`
- `Language`: `en`
- `Recognition engine`: `Vosk local`
- `Vosk fast live text`: enabled
- `Vosk sentence formatting`: enabled

For Whisper:

- `Recognition engine`: `Whisper balanced`
- `Model`: `base` for speed, `small` for higher accuracy.
- `Audio chunk length`: `3 sec` is a reasonable starting point.

## Recognition Engines

### Whisper balanced

Uses local Whisper with context from previous recognized text. It is usually more stable than raw mode but has more latency.

### Whisper fast / raw

Uses local Whisper with less context. It can be faster but may be less stable.

### Windows Speech Recognition

Uses the Windows built-in recognizer. It is available without extra model files, but quality may be poor for interview/system-audio scenarios.

### Vosk local

Uses local Vosk models. This is currently the most useful mode for responsive system-audio captions.

## Speaker Turns

The app currently has only a simple pause-based speaker-turn mode for Vosk system audio.

This is not real speaker diarization. It cannot reliably identify people by voice. It only starts a new speaker block after a detected pause and alternates `Speaker 1` / `Speaker 2`.

Real speaker diarization was intentionally not kept in the app because the tested options introduced unwanted delays or external setup requirements.

## Notes

- System audio capture depends on the default Windows playback device.
- For browser/meeting audio, make sure the sound is actually playing through the selected Windows output device.
- Vosk system audio is converted internally to `16 kHz mono PCM`.
- Whisper receives WAV chunks and therefore normally has higher latency.
- Model files and generated work files should not be committed to source control.
