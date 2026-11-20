using System;
using System.IO;
using System.Text.Json;

namespace WinPulseOptimizer;

internal sealed class UsageStatistics
{
    // A change of purpose, data fields or recipients requires a new choice.
    private const int NoticeVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _appVersion;
    private readonly string? _consentScope;
    private ConsentState? _state;
    private bool _dirty;

    internal UsageStatistics(string path, string appVersion, string? consentScope = null)
    {
        _path = path;
        _appVersion = appVersion;
        _consentScope = consentScope;
        try
        {
            var saved = JsonSerializer.Deserialize<ConsentState>(File.ReadAllText(path));
            if (saved?.NoticeVersion != NoticeVersion || saved.DecidedAtUtc == default || saved.ConsentScope != consentScope) return;
            _state = saved;
            if (saved.Summary != null && (!saved.Enabled || saved.Summary.Version != appVersion))
            {
                _state = saved with { Summary = null, Checkpoint = null };
                _dirty = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal bool NeedsDecision => _state == null;
    internal bool IsEnabled => _state?.Enabled == true;

    internal bool SetConsent(bool enabled)
    {
        if (enabled && IsEnabled) return true;
        var choice = new ConsentState(NoticeVersion, enabled, DateTimeOffset.UtcNow, null, ConsentScope: _consentScope);
        if (enabled)
        {
            if (!TrySave(choice)) return false;
            _state = choice;
            _dirty = false;
            return true;
        }

        _state = choice;
        _dirty = true;
        if (Flush()) return true;
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    internal void RecordChange(string? profile, bool? changed, int policyBlocks = 0)
    {
        if (!IsEnabled) return;
        var summary = GetSummaryForRecording();
        switch (profile)
        {
            case "JUGAR": summary.Gaming++; break;
            case "ESTUDIAR": summary.StudyWork++; break;
            case "CHILL": summary.Chill++; break;
            case { } custom when custom.StartsWith("GAME:", StringComparison.Ordinal): summary.CustomGameProfile++; break;
        }
        switch (changed)
        {
            case true: summary.Applied++; break;
            case false: summary.WithoutChanges++; break;
            default: summary.Failed++; break;
        }
        summary.BlockedByPolicy += policyBlocks;
        _dirty = true;
    }

    internal void RecordAdaptiveMemoryAdjustment()
    {
        if (!IsEnabled) return;
        GetSummaryForRecording().AdaptiveMemoryAdjustments++;
        _dirty = true;
    }

    internal UsageSummary? CurrentSummary => IsEnabled && _state?.Summary?.Version == _appVersion ? _state.Summary : null;

    private UsageSummary GetSummaryForRecording()
    {
        if (CurrentSummary is { } current) return current;
        var summary = new UsageSummary { Version = _appVersion };
        _state = _state! with { Summary = summary };
        return summary;
    }

    internal bool Flush()
    {
        if (!_dirty || _state == null) return true;
        if (!TrySave(_state)) return false;
        _dirty = false;
        return true;
    }

    internal string? GetExportJson() => CurrentSummary is { } summary ? JsonSerializer.Serialize(summary, JsonOptions) : null;

    internal UsageBatch? PrepareUpload(DateTimeOffset now)
    {
        if (_consentScope == null || _state is not { Enabled: true } state ||
            (state.LastUploadAttempt <= now && now - state.LastUploadAttempt < TimeSpan.FromDays(1))) return null;

        var pending = state.PendingUpload;
        if (pending != null && now - pending.PreparedAt >= TimeSpan.FromDays(7))
        {
            state = state with { PendingUpload = null, Checkpoint = pending.Checkpoint };
            _state = state;
            _dirty = true;
            pending = null;
        }
        if (pending == null)
        {
            if (CurrentSummary is not { } summary) return null;
            var delta = summary.DifferenceFrom(state.Checkpoint);
            if (!delta.HasEvents) return null;
            pending = new PendingUsageUpload(new UsageBatch(Guid.NewGuid(), delta), summary with { }, now);
        }

        var next = state with { PendingUpload = pending, LastUploadAttempt = now };
        if (!TrySave(next)) return null;
        _state = next;
        _dirty = false;
        return pending.Batch;
    }

    internal void ConfirmUpload(Guid batchId)
    {
        if (_state?.PendingUpload is not { } pending || pending.Batch.BatchId != batchId || !IsEnabled) return;
        _state = _state with { PendingUpload = null, Checkpoint = pending.Checkpoint };
        _dirty = true;
        Flush();
    }

    private bool TrySave(ConsentState state)
    {
        string directory = Path.GetDirectoryName(_path)!;
        string temporary = Path.Combine(directory, Guid.NewGuid() + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private sealed record ConsentState(int NoticeVersion, bool Enabled, DateTimeOffset DecidedAtUtc, UsageSummary? Summary,
        string? ConsentScope = null, UsageSummary? Checkpoint = null, PendingUsageUpload? PendingUpload = null,
        DateTimeOffset LastUploadAttempt = default);

    private sealed record PendingUsageUpload(UsageBatch Batch, UsageSummary Checkpoint, DateTimeOffset PreparedAt);
}

internal sealed record UsageBatch(Guid BatchId, UsageSummary Summary);

internal sealed record UsageSummary
{
    public string Version { get; init; } = "";
    public long Gaming { get; set; }
    public long StudyWork { get; set; }
    public long Chill { get; set; }
    public long CustomGameProfile { get; set; }
    public long Applied { get; set; }
    public long WithoutChanges { get; set; }
    public long Failed { get; set; }
    public long BlockedByPolicy { get; set; }
    public long AdaptiveMemoryAdjustments { get; set; }

    internal bool HasEvents => Applied > 0 || WithoutChanges > 0 || Failed > 0 || AdaptiveMemoryAdjustments > 0;

    internal UsageSummary DifferenceFrom(UsageSummary? previous)
    {
        if (previous == null || Version != previous.Version) return this with { };
        return new()
        {
            Version = Version,
            Gaming = Gaming - previous.Gaming,
            StudyWork = StudyWork - previous.StudyWork,
            Chill = Chill - previous.Chill,
            CustomGameProfile = CustomGameProfile - previous.CustomGameProfile,
            Applied = Applied - previous.Applied,
            WithoutChanges = WithoutChanges - previous.WithoutChanges,
            Failed = Failed - previous.Failed,
            BlockedByPolicy = BlockedByPolicy - previous.BlockedByPolicy,
            AdaptiveMemoryAdjustments = AdaptiveMemoryAdjustments - previous.AdaptiveMemoryAdjustments
        };
    }
}
