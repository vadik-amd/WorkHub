using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WorkHub;

/// <summary>
/// OCR through Windows.Media.Ocr — built into Windows, fully offline, nothing to download.
/// Reached from .NET via the WinRT projections the Windows TFM turns on (see WorkHub.csproj).
/// Measured here: ~25 ms per 720p frame.
///
/// One frame is run through every configured language and the best result is kept, because
/// the recognizer's language is decisive, not cosmetic: the English recognizer reads a
/// Russian slide as "nnaHbl I-la cneAYlOU4VlVl KBapran", while the Russian one reads both
/// "Планы на следующий квартал" and plain English correctly.
/// </summary>
public sealed class WindowsOcrEngine : IScreenTextEngine
{
    private readonly List<(OcrEngine Engine, string Tag, string Script)> _engines = new();

    /// <summary>
    /// Minimum share of a script's own letters for that recognizer to win a frame. Not a small
    /// number on purpose: the Russian recognizer sprinkles Cyrillic look-alikes into English
    /// text ("Ореп questions", "Кеер"), which is a few percent — real Cyrillic content is 80%+.
    /// </summary>
    private const double ScriptShareToWin = 0.25;

    public string Name => "Windows OCR (" + string.Join("+", _engines.Select(e => e.Tag)) + ")";

    /// <summary>Language tags installed on this Windows (OCR packs), e.g. en-US, ru, pl.</summary>
    public static List<string> AvailableLanguages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>Human-readable list for the tray menu.</summary>
    public static List<(string Tag, string Name)> AvailableLanguageNames()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => (l.LanguageTag, l.DisplayName)).ToList();
        }
        catch { return new List<(string, string)>(); }
    }

    /// <summary>
    /// Builds the engine for the requested tags (empty = every language installed). Tags that
    /// have no OCR pack are skipped; <paramref name="log"/> says what ended up being used.
    /// Throws when Windows has no OCR language at all.
    /// </summary>
    public WindowsOcrEngine(IEnumerable<string> languageTags, Action<string>? log = null)
    {
        var wanted = languageTags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (wanted.Count == 0) wanted = AvailableLanguages();

        foreach (var tag in wanted)
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
                if (engine == null)
                {
                    log?.Invoke($"OCR: язык {tag} не установлен в Windows — пропущен " +
                                "(Параметры → Время и язык → Язык → Дополнительно → Оптическое распознавание текста).");
                    continue;
                }
                var lang = engine.RecognizerLanguage;
                if (_engines.Any(e => e.Tag == lang.LanguageTag)) continue;   // ru and ru-RU are one engine
                _engines.Add((engine, lang.LanguageTag, lang.Script ?? ""));
            }
            catch (Exception ex)
            {
                log?.Invoke($"OCR: язык {tag} недоступен: {ex.Message}");
            }
        }

        if (_engines.Count == 0)
        {
            var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
            if (fallback != null)
            {
                var lang = fallback.RecognizerLanguage;
                _engines.Add((fallback, lang.LanguageTag, lang.Script ?? ""));
                log?.Invoke("OCR: беру язык из профиля Windows — " + lang.LanguageTag);
            }
        }
        if (_engines.Count == 0)
            throw new Exception("В Windows не установлен ни один язык распознавания текста (OCR).");

        // A Latin recognizer can only emit Latin letters, so Cyrillic (or any other script)
        // appearing in a non-Latin engine's output proves the frame is really in that script.
        // The reverse test is worthless, hence non-Latin engines get asked first.
        _engines.Sort((a, b) => IsLatin(a.Script).CompareTo(IsLatin(b.Script)));
    }

    public async Task<FrameText> RecognizeAsync(string framePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var bitmap = await LoadBitmapAsync(framePath);

        FrameText? latin = null, other = null;
        int latinWords = -1, otherWords = -1;
        foreach (var (engine, tag, script) in _engines)
        {
            ct.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
            if (lines.Count == 0) continue;

            string text = string.Join("\n", lines);
            var candidate = new FrameText(text, lines.Count, tag);
            int words = result.Lines.Sum(l => l.Words.Count);

            if (IsLatin(script))
            {
                if (words > latinWords) { latin = candidate; latinWords = words; }
                continue;
            }

            // A non-Latin recognizer that really found its own script has read the frame in the
            // language it is written in — take it, it also handles the Latin words correctly.
            if (ScriptShare(text, script) >= ScriptShareToWin) return candidate;
            if (words > otherWords) { other = candidate; otherWords = words; }
        }

        // No non-Latin script on this frame: the Latin recognizer is the accurate one for it
        // ("Owner", "2." — where the Russian one gives "0wner", "З.").
        return latin ?? other ?? FrameText.Empty;
    }

    private static async Task<SoftwareBitmap> LoadBitmapAsync(string path)
    {
        // Straight through memory rather than StorageFile: the frames live in a temp folder
        // and the decoder doesn't care where the bytes came from.
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }

    private static bool IsLatin(string script) =>
        script.Length == 0 || script.Equals("Latn", StringComparison.OrdinalIgnoreCase);

    /// <summary>Share of the letters in <paramref name="text"/> that belong to <paramref name="script"/>.</summary>
    private static double ScriptShare(string text, string script)
    {
        int letters = 0, own = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (string.Equals(ScriptOf(c), script, StringComparison.OrdinalIgnoreCase)) own++;
        }
        return letters == 0 ? 0 : (double)own / letters;
    }

    /// <summary>ISO 15924 tag of a letter, matching what <see cref="Language.Script"/> reports.</summary>
    private static string ScriptOf(char c) => c switch
    {
        <= 'ɏ' => "Latn",                       // Latin + supplements
        >= 'Ͱ' and <= 'Ͽ' => "Grek",
        >= 'Ѐ' and <= 'ӿ' => "Cyrl",
        >= '԰' and <= '֏' => "Armn",
        >= '֐' and <= '׿' => "Hebr",
        >= '؀' and <= 'ۿ' => "Arab",
        >= 'ऀ' and <= 'ॿ' => "Deva",
        >= '฀' and <= '๿' => "Thai",
        >= '぀' and <= 'ヿ' => "Jpan",
        >= '一' and <= '鿿' => "Hani",
        >= '가' and <= '힯' => "Hang",
        _ => "Zyyy",
    };

    public void Dispose() { /* OcrEngine holds no unmanaged handles of ours */ }
}
