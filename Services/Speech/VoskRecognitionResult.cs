namespace LiveCaptioner.Services.Speech;

public sealed record VoskRecognitionResult(string Text, float[]? SpeakerVector);
