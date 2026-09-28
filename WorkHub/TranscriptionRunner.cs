using System.Diagnostics;

namespace WorkHub;

/// <summary>One recording in one language.</summary>
public sealed class TranscriptionJob(string wav, string lang, double audioSeconds)
{
    public string Wav { get; } = wav;
    public string Lang { get; } = lang;
    public double AudioSeconds { get; } = audioSeconds;
    public string Name => Path.GetFileNameWithoutExtension(Wav) + " [" + Lang + "]";

    /// <summary>Devices this job already failed on (in distribute mode it moves on to others).</summary>
    internal HashSet<string> FailedOn { get; } = new();

    /// <summary>Devices that glitched once (transient) on this job — a second glitch rules them out.</summary>
    internal HashSet<string> Glitches { get; } = new();
}

/// <summary>What to run: jobs × executors, and how.</summary>
public sealed record TranscriptionPlan(
    IReadOnlyList<TranscriptionJob> Jobs,
    IReadOnlyList<Executor> Executors,
    bool Compare,     // true: every job on every executor; false: each job once, spread out
    bool Overwrite);  // replace a transcript with the same device/model tag if it exists

public sealed record TranscriptionSummary(int Done, int Failed, int Skipped, TimeSpan Elapsed, List<string> Errors);

/// <summary>
/// Runs a <see cref="TranscriptionPlan"/> with one worker per device (CPU, GPU and NPU work
/// in parallel; a device never runs two jobs at once).
///
/// Distribute mode schedules by speed: a free device takes a job only if it would finish
/// it no later than the other devices would (simulated from measured speeds, longest jobs
/// first) — so the CPU doesn't grab the last long recording that the GPU would do 17×
/// faster. A device that turns out unable to run its model drops out and its job goes
/// back to the others; a transient glitch (NPU hang) gets one retry first.
/// </summary>
public sealed class TranscriptionRunner
{
    private readonly Transcriber _transcriber = new();

    public event Action<string>? Log;
    public event Action<Executor, TranscriptionJob>? JobStarted;
    public event Action<Executor, TranscriptionJob, TranscriptionResult>? JobDone;
    public event Action<Executor, TranscriptionJob, string>? JobFailed;
    /// <summary>A device left the run (can't run its model / broke) — reason attached.</summary>
    public event Action<Executor, string>? ExecutorStopped;

    private sealed class Worker(Executor exec)
    {
        public Executor Exec { get; } = exec;
        public bool Alive { get; set; } = true;
        public bool Busy { get; set; }
        public double BusyUntil { get; set; } // estimated, seconds since the run started
        public TranscriptionJob? Current { get; set; }
    }

    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private Stopwatch _clock = new();

    public async Task<TranscriptionSummary> RunAsync(TranscriptionPlan plan, AppSettings settings, CancellationToken ct)
    {
        _clock = Stopwatch.StartNew();
        int done = 0, failed = 0, skipped = 0;
        var errors = new List<string>();
        var leftover = new List<TranscriptionJob>();
        void Count(ref int counter) { lock (_gate) counter++; }

        Log?.Invoke($"=== Старт: {plan.Jobs.Count} задан. × {plan.Executors.Count} устр. " +
                    $"({(plan.Compare ? "сравнение" : "распределение")}): {string.Join(", ", plan.Executors.Select(e => e.Tag))}");

        var tasks = new List<Task>();
        if (plan.Compare)
        {
            // Every executor gets every job; executors sharing a device run one after another.
            foreach (var group in plan.Executors.GroupBy(e => e.Device))
            {
                var queue = new List<(Executor, TranscriptionJob)>();
                foreach (var exec in group)
                    foreach (var job in plan.Jobs)
                    {
                        if (!plan.Overwrite && File.Exists(TranscriptPaths.For(job.Wav, job.Lang, exec))) { Count(ref skipped); continue; }
                        queue.Add((exec, job));
                    }
                bool parallel = plan.Executors.Select(e => e.Device).Distinct().Count() > 1;
                tasks.Add(Task.Run(async () =>
                {
                    var dead = new HashSet<Executor>();
                    foreach (var (exec, job) in queue)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (dead.Contains(exec)) { Count(ref failed); continue; }
                        var outcome = await RunOneAsync(exec, job, settings, parallel ? settings.ResolveWhisperThreadsParallel() : null, ct);
                        if (outcome.Ok) Count(ref done);
                        else
                        {
                            Count(ref failed);
                            lock (_gate) errors.Add($"{exec.Tag}: {job.Name}: {outcome.Error}");
                            if (outcome.DeviceBroken)
                            {
                                dead.Add(exec);
                                ExecutorStopped?.Invoke(exec, outcome.Error!);
                            }
                        }
                    }
                }, ct));
            }
        }
        else
        {
            // One executor per device.
            var workers = plan.Executors.GroupBy(e => e.Device).Select(g => new Worker(g.First())).ToList();
            var pending = plan.Jobs.OrderByDescending(j => j.AudioSeconds).ToList();

            bool Eligible(Worker w, TranscriptionJob j) =>
                !j.FailedOn.Contains(w.Exec.Device) &&
                (plan.Overwrite || !File.Exists(TranscriptPaths.For(j.Wav, j.Lang, w.Exec)));

            // Jobs no device may write (every tag exists and no overwrite) are skipped up front.
            foreach (var j in pending.Where(j => !workers.Any(w => Eligible(w, j))).ToList())
            {
                pending.Remove(j);
                skipped++;
            }
            leftover = pending;

            foreach (var w in workers)
            {
                tasks.Add(Task.Run(async () =>
                {
                    bool stalled = false;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        TranscriptionJob? job;
                        Task wait;
                        lock (_gate)
                        {
                            if (!w.Alive) return;
                            job = Pick(w, workers, pending, Eligible);
                            // Safety net: nobody has been running for a while, yet there's work I
                            // could do — never let the batch hang on estimates.
                            if (job == null && stalled && !workers.Any(o => o.Busy))
                                job = pending.FirstOrDefault(j => Eligible(w, j));
                            stalled = false;
                            if (job == null)
                            {
                                // Nothing for me now. Leave when nothing I could take is left and
                                // nobody is still running (a failing job could come back to me).
                                bool mayGetWork = pending.Any(j => Eligible(w, j));
                                if (!mayGetWork && !workers.Any(o => o.Busy)) { w.Alive = false; Pulse(); return; }
                                wait = _changed.Task;
                            }
                            else
                            {
                                pending.Remove(job);
                                w.Busy = true;
                                w.Current = job;
                                w.BusyUntil = Now + TranscriptionStats.Estimate(w.Exec, job.AudioSeconds);
                                wait = Task.CompletedTask;
                            }
                        }

                        if (job == null)
                        {
                            try { await wait.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                            catch (TimeoutException) { stalled = true; }
                            continue;
                        }

                        // whisper.cpp leaves cores for the GPU/NPU sidecars while they're busy.
                        int? threads = null;
                        if (!w.Exec.IsOpenVino)
                            lock (_gate)
                                if (workers.Any(o => o != w && o.Alive && (o.Busy || pending.Any(j => Eligible(o, j)))))
                                    threads = settings.ResolveWhisperThreadsParallel();

                        var outcome = await RunOneAsync(w.Exec, job, settings, threads, ct, retryInPlace: false);
                        lock (_gate)
                        {
                            w.Busy = false;
                            w.Current = null;
                            if (outcome.Ok) done++;
                            else
                            {
                                // A glitch doesn't rule the device out for this job until it repeats.
                                if (!outcome.Transient || job.Glitches.Add(w.Exec.Device) == false)
                                    job.FailedOn.Add(w.Exec.Device);
                                if (outcome.DeviceBroken) w.Alive = false;
                                // Hand the job to another device if one can still take it.
                                if (workers.Any(o => o.Alive && Eligible(o, job)))
                                {
                                    pending.Add(job);
                                    pending.Sort((a, b) => b.AudioSeconds.CompareTo(a.AudioSeconds));
                                    Log?.Invoke($"[{w.Exec.Tag}] {job.Name}: возвращаю в очередь — возьмёт лучшее из свободных устройств");
                                }
                                else
                                {
                                    failed++;
                                    errors.Add($"{w.Exec.Tag}: {job.Name}: {outcome.Error}");
                                }
                            }
                            Pulse();
                        }
                        if (outcome.DeviceBroken) ExecutorStopped?.Invoke(w.Exec, outcome.Error!);
                        // Give a device that just hung a moment to reset before it takes more work.
                        if (outcome.Transient) await Task.Delay(5000, ct);
                    }
                }, ct));
            }

        }

        try
        {
            await Task.WhenAll(tasks);
            // Jobs left with no live device that could take them.
            foreach (var j in leftover)
            {
                failed++;
                errors.Add($"{j.Name}: не осталось устройства, которое может его расшифровать");
            }
        }
        finally
        {
            Log?.Invoke($"=== {(ct.IsCancellationRequested ? "Остановлено" : "Завершено")}: готово {done}, " +
                        $"ошибок {failed}, пропущено {skipped}, за {_clock.Elapsed.TotalSeconds:0} с");
        }
        return new TranscriptionSummary(done, failed, skipped, _clock.Elapsed, errors);
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>
    /// How much a device's work slows the others down while it runs (they share one chip:
    /// power budget, memory bandwidth, CPU time for the sidecars). Measured on the Core Ultra 7
    /// 255H: whisper.cpp on all cores makes the GPU ~1.5× slower; the NPU barely affects anyone.
    /// </summary>
    private static double Slowdown(Executor e) =>
        !e.IsOpenVino ? 1.5 : e.Device switch { "CPU" => 1.4, "GPU" => 1.2, _ => 1.05 };

    /// <summary>
    /// The job <paramref name="w"/> should start now, or null to leave the rest to the others.
    /// Compares when the whole batch would be done (makespan) if <paramref name="w"/> takes a
    /// job now — others slowed by its contention meanwhile — against leaving everything to
    /// them. Longest eligible job first.
    /// </summary>
    private TranscriptionJob? Pick(Worker w, List<Worker> workers, List<TranscriptionJob> pending,
        Func<Worker, TranscriptionJob, bool> eligible)
    {
        var others = workers.Where(o => o != w && o.Alive).ToList();
        double without = Makespan(others, pending, eligible, 0, 1);
        foreach (var cand in pending.Where(j => eligible(w, j)))
        {
            // Nobody else can do it — it's mine whatever the estimates say.
            if (!others.Any(o => eligible(o, cand))) return cand;
            double mine = Now + TranscriptionStats.Estimate(w.Exec, cand.AudioSeconds);
            double with = Math.Max(mine, Makespan(others, pending.Where(j => j != cand), eligible, mine, Slowdown(w.Exec)));
            if (with <= without) return cand;
        }
        return null;
    }

    /// <summary>
    /// When <paramref name="others"/> would finish their current jobs plus <paramref name="jobs"/>
    /// (greedy, longest first, each to the device that finishes it earliest), with everyone
    /// running <paramref name="k"/>× slower until <paramref name="slowUntil"/>.
    /// </summary>
    private double Makespan(List<Worker> others, IEnumerable<TranscriptionJob> jobs,
        Func<Worker, TranscriptionJob, bool> eligible, double slowUntil, double k)
    {
        var t = others.ToDictionary(o => o, o => Advance(Now, o.Busy ? Math.Max(0, o.BusyUntil - Now) : 0, slowUntil, k));
        double makespan = t.Values.DefaultIfEmpty(Now).Max();
        foreach (var j in jobs)
        {
            Worker? best = null;
            double bestFinish = double.PositiveInfinity;
            foreach (var o in others.Where(o => eligible(o, j)))
            {
                double f = Advance(t[o], TranscriptionStats.Estimate(o.Exec, j.AudioSeconds), slowUntil, k);
                if (f < bestFinish) { bestFinish = f; best = o; }
            }
            if (best == null) continue; // only the deciding device could do it — same in both scenarios
            t[best] = bestFinish;
            makespan = Math.Max(makespan, bestFinish);
        }
        return makespan;
    }

    /// <summary>Finish time of <paramref name="work"/> seconds started at <paramref name="t0"/>, k× slower until <paramref name="slowUntil"/>.</summary>
    private static double Advance(double t0, double work, double slowUntil, double k)
    {
        if (work <= 0) return t0;
        if (t0 >= slowUntil || k <= 1) return t0 + work;
        double window = slowUntil - t0;
        return work * k <= window ? t0 + work * k : slowUntil + (work - window / k);
    }

    private sealed record Outcome(bool Ok, string? Error, bool DeviceBroken, bool Transient = false);

    /// <summary>
    /// Runs one job on one executor. A transient device glitch is retried here once when
    /// <paramref name="retryInPlace"/> (compare mode — it has to be this device); otherwise it's
    /// reported back so the scheduler can hand the job to whichever device is best now.
    /// </summary>
    private async Task<Outcome> RunOneAsync(Executor exec, TranscriptionJob job, AppSettings settings, int? threads,
        CancellationToken ct, bool retryInPlace = true)
    {
        string outTxt = TranscriptPaths.For(job.Wav, job.Lang, exec);
        void LogLine(string line) => Log?.Invoke($"[{exec.Tag}] {line}");

        for (int attempt = 1; ; attempt++)
        {
            JobStarted?.Invoke(exec, job);
            LogLine($"--- {job.Name} ({job.AudioSeconds:0} с) → {Path.GetFileName(outTxt)}" +
                    (!exec.IsOpenVino && threads is { } t ? $" (потоков CPU: {t})" : ""));
            try
            {
                var r = await _transcriber.TranscribeAsync(job.Wav, job.Lang, outTxt, settings, exec, threads, LogLine, ct);
                double process = r.InferSeconds ?? r.WallSeconds;
                TranscriptionStats.Record(exec, r.AudioSeconds, process, r.LoadSeconds ?? 0);
                LogLine($"Готово за {r.WallSeconds:0.0} с (RTF {(r.AudioSeconds > 0 ? r.WallSeconds / r.AudioSeconds : 0):0.00}): {outTxt}");
                JobDone?.Invoke(exec, job, r);
                return new Outcome(true, null, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Transcriber.TranscriptionFailedException ex) when (ex.Transient && attempt == 1 && retryInPlace)
            {
                LogLine($"Временный сбой, повтор через 5 с: {ex.Message}");
                await Task.Delay(5000, ct);
            }
            catch (Transcriber.TranscriptionFailedException ex) when (ex.Transient && !retryInPlace)
            {
                LogLine($"Временный сбой: {ex.Message}");
                JobFailed?.Invoke(exec, job, ex.Message);
                return new Outcome(false, ex.Message, DeviceBroken: false, Transient: true);
            }
            catch (Exception ex)
            {
                // Missing engine / model-device incompatibility / repeated glitch: this device
                // is out for the rest of the run. Anything else is specific to this job.
                bool broken = ex is Transcriber.WhisperNotFoundException ||
                              ex is Transcriber.TranscriptionFailedException;
                LogLine("Ошибка: " + ex.Message);
                JobFailed?.Invoke(exec, job, ex.Message);
                return new Outcome(false, ex.Message, broken);
            }
        }
    }

    private void Pulse()
    {
        var old = _changed;
        _changed = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
