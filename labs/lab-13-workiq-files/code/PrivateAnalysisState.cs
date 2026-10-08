using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal sealed class PrivateAnalysisOptions
{
    public bool Enabled { get; set; }
    public string StorageDirectory { get; set; } = "";
    internal string DirectoryFor(LabSettings settings)
    {
        string path = StorageDirectory.Length == 0
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgenticUserLabs", settings.TenantId + "-" + settings.BlueprintClientId, "lab13-private-analysis")
            : StorageDirectory;
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string cwd = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string checkout = cwd;
        for (DirectoryInfo? current = new(cwd); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, ".git")) || Directory.Exists(Path.Combine(current.FullName, ".git")))
            { checkout = current.FullName; break; }
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(full) != "lab13-private-analysis" ||
            full.StartsWith(checkout + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full == checkout ||
            cwd.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(full))
            throw new LabException("PrivateAnalysis storage must be an absolute dedicated lab13-private-analysis directory outside the checkout.");
        return full;
    }
}

internal static class PrivateAnalysisRegistration
{
    internal static void Register(IServiceCollection services, PrivateAnalysisOptions options, LabSettings settings)
    {
        services.AddSingleton(sp => new PrivateJobStore(
            new DpapiPrivateStateStorage(options.DirectoryFor(settings)), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<PrivateAuthorizations>();
        services.AddSingleton<IPrivateTeams, PrivateTeams>();
        services.AddSingleton<PrivateIntentRouter>();
        services.AddSingleton<PrivateAnalysisCoordinator>();
        services.AddHostedService<PrivateAnalysisWorker>();
    }
}

internal enum PrivateJobState { AwaitingRoute, AwaitingApproval, AwaitingSignIn, Ready, Running, DeliveryPending, Completed, Cancelled, Interrupted }
internal enum PrivateConsentScope { LegacyReadOnly = 0, NativeReadWriteV1 = 1 }
internal sealed record PrivateJob
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public required string Tenant { get; init; }
    public required string Request { get; init; }
    public required string Metadata { get; init; }
    public required JsonElement Origin { get; init; }
    public required string Configuration { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset Expires { get; init; }
    public PrivateJobState State { get; init; }
    // Missing on previously persisted records: never upgrade their read-only consent.
    public PrivateConsentScope ConsentScope { get; init; }
    public int Revision { get; init; } = 1;
    public JsonElement? Personal { get; init; }
    public string? MessageId { get; init; }
    public long? HumanGeneration { get; init; }
    public DateTimeOffset? ConsentUntil { get; init; }
    public bool HasRun { get; init; }
    public string? Result { get; init; }
    public string? Failure { get; init; }
    public double RoutingMs { get; init; }
    internal bool Terminal => State is PrivateJobState.Completed or PrivateJobState.Cancelled or PrivateJobState.Interrupted;
    internal string ImmutableKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Id, Owner, Tenant, Request, Metadata, Origin, Configuration, Created, Expires, RoutingMs, ConsentScope }))));
}

internal interface IPrivateStateStorage : IDisposable
{
    byte[]? Load();
    void Save(byte[] bytes);
}

internal sealed class DpapiPrivateStateStorage : IPrivateStateStorage
{
    private const string Marker = "WorkIQ Lab13 private analysis state v1";
    private readonly string file, temporary;
    private readonly FileStream lease;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(Marker);
    internal DpapiPrivateStateStorage(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new LabException("PrivateAnalysis local persistence requires Windows current-user DPAPI.");
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LabException("Private state directory cannot traverse links.");
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "owner.txt");
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new LabException("Private state directory is not empty and has no lab ownership marker; no files replaced.");
            using FileStream created = new(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Write(Entropy); created.Flush(true);
        }
        if (File.ReadAllText(marker) != Marker || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new LabException("Private state directory ownership is not established.");
        file = Path.Combine(directory, "jobs.dpapi");
        temporary = Path.Combine(directory, "jobs.pending");
        foreach (string path in new[] { marker, file, temporary, Path.Combine(directory, "host.lock") })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new LabException("Private state links are not supported.");
        lease = new(Path.Combine(directory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // A leftover uncommitted snapshot indicates interrupted persistence; do not guess which state executed.
        if (File.Exists(temporary)) { lease.Dispose(); throw new LabException("Private state has an interrupted save; offline operator recovery required."); }
    }
    public byte[]? Load()
    {
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new LabException("Private state file exceeds its bound.");
        return ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
    }
    public void Save(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new LabException("Private state exceeds its bound.");
        byte[] encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(encrypted); output.Flush(true); }
        if (File.Exists(file)) File.Replace(temporary, file, null);
        else File.Move(temporary, file);
    }
    public void Dispose() => lease.Dispose();
}

internal sealed class PrivateJobStore : IDisposable
{
    private readonly object sync = new();
    private readonly IPrivateStateStorage storage;
    private readonly TimeProvider clock;
    private Dictionary<string, PrivateJob> jobs = [];
    private bool faulted;
    internal PrivateJobStore(IPrivateStateStorage storage, TimeProvider clock)
    {
        this.storage = storage; this.clock = clock;
        try
        {
            byte[]? bytes = storage.Load();
            if (bytes is not null)
            {
                jobs = JsonSerializer.Deserialize<Dictionary<string, PrivateJob>>(bytes)
                    ?? throw new LabException("Private state is invalid.");
                Validate(jobs);
                jobs = jobs.Where(p => p.Value.Expires > clock.GetUtcNow()).ToDictionary(p => p.Key, p =>
                {
                    PrivateJob j = p.Value;
                    return j.Terminal ? j : j with
                    {
                        State = j.State == PrivateJobState.Running ? PrivateJobState.Interrupted :
                            j.Personal is null ? PrivateJobState.AwaitingRoute : PrivateJobState.AwaitingApproval,
                        Revision = checked(j.Revision + 1), HumanGeneration = null, ConsentUntil = null,
                        Failure = j.State == PrivateJobState.Running ? "Interrupted; analysis is never automatically replayed." : null
                    };
                });
                Save(jobs);
            }
        }
        catch (Exception error) when (error is JsonException or CryptographicException or IOException or UnauthorizedAccessException)
        { storage.Dispose(); throw new LabException("Private persisted state could not be loaded; no jobs executed or reset. Details withheld."); }
        catch { storage.Dispose(); throw; }
    }
    internal PrivateJob Add(PrivateJob job)
    {
        lock (sync)
        {
            Ensure();
            Dictionary<string, PrivateJob> copy = Active();
            if (copy.Count >= 64 || copy.ContainsKey(job.Id)) throw new LabException("Private pending-job limit or duplicate request.");
            copy.Add(job.Id, job); Save(copy); jobs = copy; return job;
        }
    }
    internal PrivateJob Get(string id)
    {
        lock (sync)
        {
            Ensure();
            if (!jobs.TryGetValue(id, out PrivateJob? job) || job.Expires <= clock.GetUtcNow())
                throw new LabException("Private request unavailable or expired.");
            return job;
        }
    }
    internal PrivateJob Change(string id, Func<PrivateJob, PrivateJob> update)
    {
        lock (sync)
        {
            PrivateJob old = Get(id), changed = update(old);
            if (changed.ImmutableKey != old.ImmutableKey) throw new LabException("Immutable private request changed.");
            Dictionary<string, PrivateJob> copy = Active(); copy[id] = changed;
            Save(copy); jobs = copy; return changed;
        }
    }
    internal PrivateJob[] List()
    {
        lock (sync)
        {
            Ensure();
            Dictionary<string, PrivateJob> active = Active();
            if (active.Count != jobs.Count) { Save(active); jobs = active; }
            return jobs.Values.OrderBy(j => j.Created).ToArray();
        }
    }
    private Dictionary<string, PrivateJob> Active() => jobs.Where(p => p.Value.Expires > clock.GetUtcNow()).ToDictionary();
    private void Save(Dictionary<string, PrivateJob> value)
    {
        Validate(value);
        try { storage.Save(JsonSerializer.SerializeToUtf8Bytes(value)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or LabException)
        { faulted = true; throw new LabException("Private state persistence failed; execution disabled. Details withheld."); }
    }
    private static void Validate(Dictionary<string, PrivateJob> value)
    {
        if (value.Count > 64) throw new LabException("Private state count exceeds limit.");
        foreach ((string key, PrivateJob j) in value)
        {
            if (j is null || key != j.Id || key.Length != 48 || key.Any(c => !char.IsAsciiHexDigit(c)) ||
                !Guid.TryParse(j.Owner, out _) || !Guid.TryParse(j.Tenant, out _))
                throw new PrivateStateValidationException(PrivateStateInvariant.RecordIdentity);
            if (j.Request is null || j.Request.Length is < 1 or > 16000)
                throw new PrivateStateValidationException(PrivateStateInvariant.RequestLength, j.Request?.Length, 16000, PrivateLimitUnit.Characters);
            if (j.Metadata is null || j.Metadata.Length > 16000)
                throw new PrivateStateValidationException(PrivateStateInvariant.MetadataLength, j.Metadata?.Length, 16000, PrivateLimitUnit.Characters);
            if (j.Origin.ValueKind != JsonValueKind.Object || j.Personal is { ValueKind: not JsonValueKind.Object })
                throw new PrivateStateValidationException(PrivateStateInvariant.ContextShape);
            if (j.Configuration is null || j.Configuration.Length != 64)
                throw new PrivateStateValidationException(PrivateStateInvariant.ConfigurationShape);
            if (j.MessageId?.Length > 2048)
                throw new PrivateStateValidationException(PrivateStateInvariant.MessageIdLength, j.MessageId.Length, 2048, PrivateLimitUnit.Characters);
            if (j.Expires <= j.Created)
                throw new PrivateStateValidationException(PrivateStateInvariant.LifetimeOrder);
            if (j.Expires - j.Created > TimeSpan.FromMinutes(30))
                throw new PrivateStateValidationException(PrivateStateInvariant.LifetimeLimit,
                    (j.Expires - j.Created).Ticks, TimeSpan.FromMinutes(30).Ticks, PrivateLimitUnit.Ticks);
            if (j.Revision < 1) throw new PrivateStateValidationException(PrivateStateInvariant.Revision);
            if (!Enum.IsDefined(j.State)) throw new PrivateStateValidationException(PrivateStateInvariant.State);
            if (!Enum.IsDefined(j.ConsentScope)) throw new PrivateStateValidationException(PrivateStateInvariant.ConsentScope);
            if (j.Result?.Length > 24576)
                throw new PrivateStateValidationException(PrivateStateInvariant.ResultLength, j.Result.Length, 24576, PrivateLimitUnit.Characters);
            int bytes = JsonSerializer.SerializeToUtf8Bytes(j).Length;
            if (bytes > 128 * 1024)
                throw new PrivateStateValidationException(PrivateStateInvariant.RecordBytes, bytes, 128 * 1024, PrivateLimitUnit.Bytes);
            if (j.HasRun && j.State == PrivateJobState.Ready && j.Result is null)
                throw new PrivateStateValidationException(PrivateStateInvariant.ClaimedReadyWithoutResult);
        }
    }
    private void Ensure() { if (faulted) throw new LabException("Private state unavailable; no execution."); }
    public void Dispose() => storage.Dispose();
}
