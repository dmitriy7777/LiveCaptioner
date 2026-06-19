namespace LiveCaptioner.Services.Speech;

public sealed record OpenAITranscriptionResult(string Text, IReadOnlyList<OpenAISpeakerSegment> Segments)
{
    public bool HasSpeakerSegments => Segments.Count > 0;
}

public sealed record OpenAISpeakerSegment(string Speaker, string Text, double? Start, double? End);
