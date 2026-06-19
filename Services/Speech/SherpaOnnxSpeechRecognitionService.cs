namespace LiveCaptioner.Services.Speech;

public sealed class SherpaOnnxSpeechRecognitionService
{
    private readonly string _projectRoot;

    public SherpaOnnxSpeechRecognitionService(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public string RuntimePath => System.IO.Path.Combine(_projectRoot, "Tools", "sherpa-onnx", "sherpa-onnx.exe");

    public bool HasRuntime => System.IO.File.Exists(RuntimePath);

    public Task<string> TranscribeAsync(byte[] wavBytes, string language, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Sherpa-ONNX is selectable, but the local runtime bridge is not installed yet. " +
            $"Expected runtime: {RuntimePath}");
    }
}
