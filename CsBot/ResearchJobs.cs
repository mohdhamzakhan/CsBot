using System.Collections.Concurrent;

namespace CsBot.Research;

public enum JobState { Queued, Running, Done, Failed, Cancelled }

public sealed class ResearchJob
{
    private double _progress;
    private volatile JobState _state = JobState.Queued;
    private volatile string? _error;
    private volatile object? _result;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "";
    public DateTime Created { get; } = DateTime.UtcNow;
    public JobState State { get => _state; set => _state = value; }
    public double Progress { get => Volatile.Read(ref _progress); set => Volatile.Write(ref _progress, value); }
    public string? Error { get => _error; set => _error = value; }
    public object? Result { get => _result; set => _result = value; }
    internal CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Runs research work in the background, one job at a time (each job already uses all cores),
/// so an HTTP request returns immediately with a job id that the UI polls.
/// </summary>
public sealed class ResearchJobs
{
    private readonly ConcurrentDictionary<string, ResearchJob> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ResearchJob Start(string kind, Func<Action<double>, CancellationToken, object> work)
    {
        Prune();
        var job = new ResearchJob { Kind = kind };
        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(job.Cts.Token);
                try
                {
                    job.State = JobState.Running;
                    job.Result = work(p => job.Progress = Math.Clamp(p, 0, 1), job.Cts.Token);
                    job.Progress = 1;
                    job.State = JobState.Done;
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) { job.State = JobState.Cancelled; }
            catch (Exception ex) { job.Error = ex.Message; job.State = JobState.Failed; }
        });
        return job;
    }

    public ResearchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var j) || j.State is JobState.Done or JobState.Failed or JobState.Cancelled) return false;
        j.Cts.Cancel();
        return true;
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        foreach (var kv in _jobs.Where(kv => kv.Value.Created < cutoff && kv.Value.State != JobState.Running))
            _jobs.TryRemove(kv.Key, out _);
    }
}
