# LiveCaptioner

Desktop WPF prototype for live captions from Windows system audio.

## What works

- Captures microphone audio.
- Captures Windows system audio with WASAPI loopback.
- Shows live audio level.
- Splits audio into short chunks and transcribes them with local Whisper.net.
- Can download `ggml-base.bin` from the app.
- Saves the current transcript to `.txt`.
- Optional always-on-top window.

## First run

1. Build and run the app.
2. Click `Скачать base-модель`, or place `ggml-base.bin` into:

   `C:\Projects\LiveCaptioner\Models\ggml-base.bin`

3. Select `Микрофон` if you want to speak yourself, or `Системный звук Windows` for meeting/browser/YouTube audio.
4. Click `Старт`.

## Notes

- Recognition is local and CPU-based by default, so text appears with a delay of several seconds.
- The model is stored in the project-level `Models` folder, so it survives Debug/Release rebuilds.
- Speaker roles are not implemented yet. The current app receives mixed system audio, so real role separation needs diarization or an API/service that supports speaker separation.
