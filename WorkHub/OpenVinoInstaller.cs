using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WorkHub;

/// <summary>
/// One-time setup of the OpenVINO GenAI Whisper engine into %APPDATA%\WorkHub\tools\openvino
/// — no admin, no global Python:
///   • uv.exe (GitHub, astral-sh/uv) bootstraps a portable CPython 3.12 (python-build-standalone,
///     also GitHub) and a private venv, then pip-installs openvino-genai from PyPI;
///   • a ready-made OpenVINO IR Whisper model (no torch/optimum conversion needed), from
///     huggingface.co → hf-mirror.com → modelscope.cn, or a folder the user points at.
/// </summary>
public static class OpenVinoInstaller
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WorkHub", "1.0"));
        return c;
    }

    public static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHub", "tools", "openvino");

    private static string UvExe => Path.Combine(ToolsDir, "uv", "uv.exe");
    private static string PythonDir => Path.Combine(ToolsDir, "python");
    private static string VenvDir => Path.Combine(ToolsDir, "venv");
    public static string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");
    public static string ModelsDir => Path.Combine(ToolsDir, "models");
    public static string CacheDir => Path.Combine(ToolsDir, "cache");
    public static string ScriptPath => Path.Combine(ToolsDir, "ov_whisper.py");

    // Pinned so an upstream release can't silently change behaviour; bump deliberately.
    private const string GenAiRequirement = "openvino-genai==2026.4.*";
    private const string PythonVersion = "3.12";

    private const string CompleteMarker = ".workhub_complete";

    /// <summary>Files WhisperPipeline needs in an IR model folder.</summary>
    private static readonly string[] RequiredModelFiles =
    {
        "openvino_encoder_model.xml", "openvino_encoder_model.bin",
        "openvino_decoder_model.xml", "openvino_decoder_model.bin",
        "openvino_tokenizer.xml", "openvino_tokenizer.bin",
        "openvino_detokenizer.xml", "openvino_detokenizer.bin",
        "config.json", "generation_config.json", "preprocessor_config.json",
    };

    /// <summary>Ready-made IR models offered in the tray (repo, label, NPU note from testing).</summary>
    public static readonly (string Repo, string Label)[] KnownModels =
    {
        ("OpenVINO/whisper-large-v3-turbo-int8-ov", "large-v3-turbo int8 — рекомендуется (~0.8 ГБ, NPU и GPU, самая быстрая)"),
        ("OpenVINO/whisper-large-v3-int8-ov", "large-v3 int8 (~1.5 ГБ, чуть точнее; на NPU не работает → GPU)"),
        ("OpenVINO/whisper-medium-int8-ov", "medium int8 (~0.75 ГБ, с пунктуацией, но выдумывает фразы)"),
    };

    /// <summary>Files VLMPipeline needs in an IR model folder (the screen-text VLM layer).</summary>
    private static readonly string[] RequiredVlmFiles =
    {
        "openvino_language_model.xml", "openvino_language_model.bin",
        "openvino_text_embeddings_model.xml", "openvino_text_embeddings_model.bin",
        "openvino_vision_embeddings_model.xml", "openvino_vision_embeddings_model.bin",
        "openvino_tokenizer.xml", "openvino_tokenizer.bin",
        "openvino_detokenizer.xml", "openvino_detokenizer.bin",
        "config.json",
    };

    /// <summary>
    /// Ready-made vision-language IR models offered for the screen-text VLM layer. Sizes are
    /// the repos' actual totals; all of them run through the same NPU → GPU → CPU chain.
    /// </summary>
    public static readonly (string Repo, string Label)[] KnownVlmModels =
    {
        ("OpenVINO/InternVL2-1B-int4-ov", "InternVL2 1B int4 — рекомендуется (~0.8 ГБ, самая быстрая)"),
        ("OpenVINO/InternVL2-2B-int4-ov", "InternVL2 2B int4 (~1.5 ГБ, заметно точнее описывает экран)"),
        ("OpenVINO/Phi-3.5-vision-instruct-int4-ov", "Phi-3.5-vision int4 (~2.3 ГБ, хорошо читает интерфейсы)"),
        ("OpenVINO/InternVL2-4B-int4-ov", "InternVL2 4B int4 (~2.3 ГБ, ещё точнее, медленнее)"),
        ("OpenVINO/Qwen3-VL-4B-Instruct-int4-ov", "Qwen3-VL 4B int4 (~3 ГБ, самая свежая, требовательная)"),
    };

    /// <summary>Where a downloaded VLM model lives.</summary>
    public static string VlmModelDirFor(string repo) => Path.Combine(ModelsDir, repo.Split('/').Last());

    public static List<string> MissingVlmFiles(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return RequiredVlmFiles.ToList();
        return RequiredVlmFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
    }

    /// <summary>True when <paramref name="repo"/> is fully downloaded as a VLM model.</summary>
    public static bool IsVlmModelDownloaded(string repo)
    {
        string dir = VlmModelDirFor(repo);
        return File.Exists(Path.Combine(dir, CompleteMarker)) && MissingVlmFiles(dir).Count == 0;
    }

    /// <summary>
    /// Downloads a ready-made VLM model (no conversion, no optimum-cli) and returns its folder.
    /// Same sources and resume behaviour as the Whisper models.
    /// </summary>
    public static async Task<string> EnsureVlmModelAsync(string repo, Action<string> log, CancellationToken ct)
    {
        string dir = VlmModelDirFor(repo);
        if (IsVlmModelDownloaded(repo))
        {
            log("Модель уже загружена: " + dir);
            return dir;
        }

        await DownloadRepoAsync(repo, dir, log, ct);

        var missing = MissingVlmFiles(dir);
        if (missing.Count > 0)
            throw new Exception($"В скачанной модели нет файлов: {string.Join(", ", missing)}. " +
                                "Похоже, это не VLM-модель для OpenVINO GenAI.");
        return dir;
    }

    /// <summary>Where the installer keeps a downloaded repo.</summary>
    public static string ModelDirFor(string repo) => Path.Combine(ModelsDir, repo.Split('/').Last());

    /// <summary>True when <paramref name="repo"/> is fully downloaded.</summary>
    public static bool IsModelDownloaded(string repo)
    {
        string dir = ModelDirFor(repo);
        return File.Exists(Path.Combine(dir, CompleteMarker)) && MissingModelFiles(dir).Count == 0;
    }

    public static string ModelPageUrl(AppSettings s) => $"https://huggingface.co/{s.OpenVinoModelRepo}";

    /// <summary>True when the venv python and a valid IR model are both in place.</summary>
    public static bool IsReady(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.OpenVinoPython) && File.Exists(s.OpenVinoPython) &&
        MissingModelFiles(s.OpenVinoModelDir).Count == 0;

    public static List<string> MissingModelFiles(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return RequiredModelFiles.ToList();
        return RequiredModelFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
    }

    /// <summary>Full setup: runtime (uv + Python + openvino-genai) and the default model.</summary>
    public static async Task InstallAsync(AppSettings settings, Action<string> log, CancellationToken ct,
        bool switchToRepo = false)
    {
        await EnsureRuntimeAsync(settings, log, ct);
        await EnsureModelAsync(settings, log, ct, switchToRepo);
        log("Готово. OpenVINO настроен.");
    }

    /// <summary>
    /// Points the engine at a user-supplied IR model folder (for when every model source is
    /// blocked). Throws with the list of missing files if the folder isn't a Whisper IR model.
    /// </summary>
    public static void SetModelDir(AppSettings settings, string dir)
    {
        var missing = MissingModelFiles(dir);
        if (missing.Count > 0)
            throw new Exception(
                "В папке нет файлов OpenVINO IR-модели Whisper: " + string.Join(", ", missing) +
                ". Нужна папка, как в репозитории " + settings.OpenVinoModelRepo +
                " (или результат `optimum-cli export openvino`).");
        settings.OpenVinoModelDir = dir;
        settings.Save();
    }

    // ------------------------------------------------------------------ runtime

    /// <summary>uv → portable Python venv → openvino-genai, then a device probe.</summary>
    public static async Task EnsureRuntimeAsync(AppSettings settings, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        await EnsureUvAsync(log, ct);

        if (!File.Exists(VenvPython))
        {
            log($"Подготовка портативного Python {PythonVersion} (~20 МБ, GitHub)…");
            await RunUvAsync(new[] { "venv", "--python", PythonVersion, VenvDir }, log, ct);
        }
        else
        {
            log("Python уже подготовлен.");
        }

        if (!await ProbeImportAsync(log, ct))
        {
            log($"Установка {GenAiRequirement} (~100 МБ, PyPI)…");
            await RunUvAsync(new[] { "pip", "install", "--python", VenvPython, GenAiRequirement, "numpy" }, log, ct);
            if (!await ProbeImportAsync(log, ct))
                throw new Exception("openvino-genai установлен, но не импортируется — см. лог выше.");
            // uv keeps a copy of every downloaded wheel (~100 МБ); it's not needed afterwards.
            try { Directory.Delete(Path.Combine(ToolsDir, "uv-cache"), recursive: true); } catch { }
        }
        else
        {
            log("openvino-genai уже установлен.");
        }

        EnsureScript();
        settings.OpenVinoPython = VenvPython;
        settings.Save();
    }

    private static async Task EnsureUvAsync(Action<string> log, CancellationToken ct)
    {
        if (File.Exists(UvExe)) return;

        log("Поиск uv на GitHub…");
        var apiUrl = "https://api.github.com/repos/astral-sh/uv/releases/latest";
        using var resp = await Http.GetAsync(apiUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() == "uv-x86_64-pc-windows-msvc.zip")
                url = a.GetProperty("browser_download_url").GetString();
        }
        if (url == null) throw new Exception("В последнем релизе uv нет сборки для Windows x64.");

        string zip = Path.Combine(ToolsDir, "uv.zip");
        await DownloadFileAsync(url, zip, "uv (~18 МБ)", log, ct);
        string uvDir = Path.GetDirectoryName(UvExe)!;
        if (Directory.Exists(uvDir)) Directory.Delete(uvDir, recursive: true);
        ZipFile.ExtractToDirectory(zip, uvDir, overwriteFiles: true);
        try { File.Delete(zip); } catch { }
        if (!File.Exists(UvExe)) throw new Exception("В архиве uv не найден uv.exe.");
    }

    /// <summary>Imports openvino_genai in the venv and logs the devices OpenVINO sees.</summary>
    private static async Task<bool> ProbeImportAsync(Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(VenvPython)) return false;
        var lines = new List<string>();
        int code = await RunAsync(VenvPython, new[]
        {
            "-c",
            "import openvino as ov, openvino_genai as g; " +
            "c = ov.Core(); print('OpenVINO', g.__version__, '| устройства:', " +
            "', '.join(d + ' (' + c.get_property(d, 'FULL_DEVICE_NAME') + ')' for d in c.available_devices))",
        }, null, l => lines.Add(l), ct);
        if (code != 0) return false;
        foreach (var l in lines) log(l);
        return true;
    }

    /// <summary>
    /// Runs uv. The system proxy on this kind of machine may be a SOCKS entry ("socks=host:port")
    /// that uv misreads as an HTTP proxy host, so first go direct (like .NET's HttpClient does),
    /// then retry through the system proxy spelled out explicitly.
    /// </summary>
    private static async Task RunUvAsync(string[] args, Action<string> log, CancellationToken ct)
    {
        var baseEnv = new Dictionary<string, string>
        {
            ["UV_PYTHON_INSTALL_DIR"] = PythonDir,
            ["UV_CACHE_DIR"] = Path.Combine(ToolsDir, "uv-cache"),
            ["UV_PYTHON_PREFERENCE"] = "only-managed", // never pick up / touch a system Python
            ["UV_NO_CONFIG"] = "1",
        };

        var direct = new Dictionary<string, string>(baseEnv) { ["NO_PROXY"] = "*" };
        if (await RunAsync(UvExe, args, direct, log, ct) == 0) return;

        var proxy = SystemProxyEnv();
        if (proxy.Count > 0)
        {
            log("Прямое соединение не удалось — пробую через системный прокси…");
            var viaProxy = new Dictionary<string, string>(baseEnv);
            foreach (var (k, v) in proxy) viaProxy[k] = v;
            if (await RunAsync(UvExe, args, viaProxy, log, ct) == 0) return;
        }

        throw new Exception(
            "uv завершился с ошибкой (см. лог). Нужен доступ к github.com (Python) и pypi.org / " +
            "files.pythonhosted.org (пакеты).");
    }

    /// <summary>Translates the WinINet proxy (HKCU Internet Settings) into uv's env vars.</summary>
    private static Dictionary<string, string> SystemProxyEnv()
    {
        var env = new Dictionary<string, string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0) return env;
            if (key.GetValue("ProxyServer") is not string server || string.IsNullOrWhiteSpace(server)) return env;

            // Either "host:port" for everything, or "http=h:p;https=h:p;socks=h:p".
            var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && !parts[0].Contains('='))
            {
                env["HTTP_PROXY"] = env["HTTPS_PROXY"] = "http://" + parts[0];
                return env;
            }
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;
                switch (kv[0].ToLowerInvariant())
                {
                    case "https": env["HTTPS_PROXY"] = "http://" + kv[1]; break;
                    case "http": env["HTTP_PROXY"] = "http://" + kv[1]; break;
                    case "socks": env["ALL_PROXY"] = "socks5h://" + kv[1]; break;
                }
            }
        }
        catch { /* no usable proxy info */ }
        return env;
    }

    private static readonly object ScriptGate = new();
    private static readonly HashSet<string> WrittenScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the embedded sidecar script next to the venv (refreshing it after hub updates).
    /// Once per hub session and under a lock: several devices start sidecars at the same moment.
    /// </summary>
    public static string EnsureScript() => EnsureScript("ov_whisper.py");

    /// <summary>Same, for another embedded sidecar (e.g. the screen VLM).</summary>
    public static string EnsureScript(string fileName)
    {
        lock (ScriptGate)
        {
            string path = Path.Combine(ToolsDir, fileName);
            if (WrittenScripts.Contains(fileName) && File.Exists(path)) return path;

            using var res = typeof(OpenVinoInstaller).Assembly.GetManifestResourceStream("WorkHub." + fileName)
                            ?? throw new Exception($"В сборке нет ресурса {fileName}.");
            using var reader = new StreamReader(res, Encoding.UTF8);
            string script = reader.ReadToEnd();

            Directory.CreateDirectory(ToolsDir);
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != script)
            {
                // Write aside and swap in, so a python that is reading the old copy isn't disturbed.
                string tmp = path + ".new";
                File.WriteAllText(tmp, script, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            WrittenScripts.Add(fileName);
            return path;
        }
    }

    // ------------------------------------------------------------------ model

    private sealed record ModelSource(string Name, Func<string, CancellationToken, Task<List<(string Path, long Size)>>> List,
        Func<string, string, string> FileUrl);

    private static readonly ModelSource[] ModelSources =
    {
        new("huggingface.co", (repo, ct) => ListHfAsync("https://huggingface.co", repo, ct),
            (repo, f) => $"https://huggingface.co/{repo}/resolve/main/{f}"),
        new("hf-mirror.com", (repo, ct) => ListHfAsync("https://hf-mirror.com", repo, ct),
            (repo, f) => $"https://hf-mirror.com/{repo}/resolve/main/{f}"),
        new("modelscope.cn", ListModelScopeAsync,
            (repo, f) => $"https://www.modelscope.cn/models/{repo}/resolve/master/{f}"),
    };

    /// <summary>
    /// Makes settings.OpenVinoModelRepo the active model, downloading it if needed. A model
    /// folder the user picked by hand is kept as long as it's still valid, unless
    /// <paramref name="switchToRepo"/> asks for the repo explicitly (tray model choice).
    /// </summary>
    public static async Task EnsureModelAsync(AppSettings settings, Action<string> log, CancellationToken ct,
        bool switchToRepo = false)
    {
        if (!switchToRepo && MissingModelFiles(settings.OpenVinoModelDir).Count == 0)
        {
            log("Модель уже на месте: " + settings.OpenVinoModelDir);
            return;
        }

        string repo = settings.OpenVinoModelRepo;
        string dir = ModelDirFor(repo);
        if (IsModelDownloaded(repo))
        {
            log("Модель уже загружена.");
            SetModelDir(settings, dir);
            return;
        }

        await DownloadRepoAsync(repo, dir, log, ct);
        SetModelDir(settings, dir);
    }

    /// <summary>
    /// Downloads every root file of a model repo into <paramref name="dir"/>, taking the first
    /// source that answers. Files already there with the right size are kept, so an interrupted
    /// download resumes cheaply. Used for both the Whisper and the VLM models.
    /// </summary>
    public static async Task DownloadRepoAsync(string repo, string dir, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var errors = new List<string>();
        foreach (var src in ModelSources)
        {
            try
            {
                log($"Модель {repo}: источник {src.Name}…");
                var files = (await src.List(repo, ct))
                    .Where(f => !f.Path.Contains('/') && f.Path is not (".gitattributes" or "README.md"))
                    .ToList();
                if (files.Count == 0) throw new Exception("пустой список файлов");
                log($"  файлов: {files.Count}, всего {Mb(files.Sum(f => f.Size))}");

                foreach (var (name, size) in files)
                {
                    string dest = Path.Combine(dir, name);
                    // Mirrors can hold different revisions of the model; only keep a file whose
                    // size matches this source exactly, so xml/bin pairs never get mixed up.
                    if (File.Exists(dest) && (size <= 0 || new FileInfo(dest).Length == size)) continue;
                    await DownloadFileAsync(src.FileUrl(repo, name), dest, $"{name} ({Mb(size)})", log, ct);
                    if (size > 0 && new FileInfo(dest).Length != size)
                        throw new Exception($"{name}: размер не совпал");
                }

                File.WriteAllText(Path.Combine(dir, CompleteMarker), $"{src.Name} {repo} {DateTime.Now:s}");
                log("Модель загружена: " + dir);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"  {src.Name}: не удалось ({ex.Message})");
                errors.Add($"{src.Name}: {ex.Message}");
            }
        }

        throw new Exception(
            "Не удалось скачать модель ни с одного источника (" + string.Join("; ", errors) + "). " +
            $"Скачайте папку модели {repo} вручную (huggingface.co / hf-mirror.com / modelscope.cn) " +
            "и укажите её кнопкой «Указать папку модели…». Папка хаба: " + ModelsDir);
    }

    private static async Task<List<(string, long)>> ListHfAsync(string host, string repo, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{host}/api/models/{repo}?blobs=true", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<(string, long)>();
        foreach (var s in doc.RootElement.GetProperty("siblings").EnumerateArray())
        {
            string name = s.GetProperty("rfilename").GetString() ?? "";
            long size = s.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            if (name.Length > 0) list.Add((name, size));
        }
        return list;
    }

    private static async Task<List<(string, long)>> ListModelScopeAsync(string repo, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(
            $"https://www.modelscope.cn/api/v1/models/{repo}/repo/files?Revision=master&Recursive=true", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<(string, long)>();
        foreach (var f in doc.RootElement.GetProperty("Data").GetProperty("Files").EnumerateArray())
        {
            if (f.GetProperty("Type").GetString() != "blob") continue;
            list.Add((f.GetProperty("Path").GetString() ?? "", f.GetProperty("Size").GetInt64()));
        }
        return list;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs a hidden process, streaming its output to <paramref name="log"/>; kills it on cancel.</summary>
    private static async Task<int> RunAsync(string exe, IEnumerable<string> args,
        IDictionary<string, string>? env, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("  " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("  " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return p.ExitCode;
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, string label, Action<string> log, CancellationToken ct)
    {
        log($"Загрузка: {label}…");
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        string tmp = destPath + ".part";

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long read = 0, lastReported = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (read - lastReported >= 50_000_000) // report every ~50 MB
                {
                    lastReported = read;
                    log(total.HasValue
                        ? $"  {Mb(read)} / {Mb(total.Value)} ({read * 100 / total.Value}%)"
                        : $"  {Mb(read)}");
                }
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
    }

    private static string Mb(long bytes) => $"{bytes / 1_048_576.0:0.0} МБ";
}
