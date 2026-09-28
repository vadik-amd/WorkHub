namespace WorkHub;

/// <summary>Which pipeline the screen-text feature runs (see <see cref="AppSettings.ScreenUnderstanding"/>).</summary>
public enum ScreenMode
{
    Off,
    Ocr,
    OcrKeyFrames,
    Vlm,
}

/// <summary>What one engine made of one key frame.</summary>
/// <param name="Text">Recognized text (or a description, for a VLM), already trimmed.</param>
/// <param name="Lines">Number of text lines found — 0 means "nothing readable here".</param>
/// <param name="Detail">Engine-specific note for the log: the language picked, the device used…</param>
public sealed record FrameText(string Text, int Lines, string? Detail = null)
{
    public static readonly FrameText Empty = new("", 0);
}

/// <summary>
/// Turns one frame image into text. The only implementation today is
/// <see cref="WindowsOcrEngine"/> (Windows.Media.Ocr — offline, no models to download);
/// the interface is here so another engine (RapidOCR / PaddleOCR-ONNX) or the optional
/// <see cref="ScreenVlmEngine"/> can be dropped in without touching the pipeline.
/// </summary>
public interface IScreenTextEngine : IDisposable
{
    /// <summary>Short name for the log and the timeline header.</summary>
    string Name { get; }

    /// <summary>Recognizes one frame; returns <see cref="FrameText.Empty"/> when nothing is readable.</summary>
    Task<FrameText> RecognizeAsync(string framePath, CancellationToken ct);
}
