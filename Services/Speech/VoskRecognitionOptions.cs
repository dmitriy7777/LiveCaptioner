namespace LiveCaptioner.Services.Speech;

public sealed record VoskRecognitionOptions(
    bool EnablePartialResults,
    bool UseInterviewVocabulary,
    double SystemAudioGain,
    bool SystemAudioNoiseGate);
