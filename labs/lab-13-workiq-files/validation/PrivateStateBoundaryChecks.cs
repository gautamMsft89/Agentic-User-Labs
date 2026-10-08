using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private sealed class AdvancingPrivateClock(DateTimeOffset start, long stepTicks) : TimeProvider
    {
        private long reads;
        public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Increment(ref reads) * stepTicks);
    }
    private static PrivateAnalysisCoordinator PrivateCoordinatorWithClock(PrivateHarness p, TimeProvider clock) =>
        new(p.Options, p.Store, p.Authorization, p.Humans, p.H.HumanSettings, p.Teams,
            new(p.ModelOptions, o => new NaturalLanguageModel(o, p.Routing)), p.H.Policy, p.H.Settings, clock, p.Log);
    private static async Task PrivateStateBoundaryChecks(Func<string, Func<Task>, Task> check)
    {
        await check("Private original separate timestamp reads reproduce JobPersist lifetime rejection without saving", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob template = await p.Initiate();
            byte[] before = p.Storage.Bytes!.ToArray();
            AdvancingPrivateClock clock = new(p.H.Clock.GetUtcNow(), 1);
            // Reproduce the previous construction exactly: the second read advances the expiry beyond the invariant.
            PrivateJob prior = template with
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                Created = clock.GetUtcNow(), Expires = clock.GetUtcNow().AddMinutes(30)
            };
            PrivateStateValidationException? rejected = null;
            try { p.Store.Add(prior); } catch (PrivateStateValidationException error) { rejected = error; }
            Must(rejected?.Invariant == PrivateStateInvariant.LifetimeLimit &&
                rejected.Observed == TimeSpan.FromMinutes(30).Ticks + 1 &&
                rejected.Maximum == TimeSpan.FromMinutes(30).Ticks &&
                before.SequenceEqual(p.Storage.Bytes!) && p.Store.List().Length == 1);
            using PrivateIngressDiagnostic diagnostic = new();
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.JobPersist);
            diagnostic.Persistence = PrivatePersistence.Unconfirmed;
            diagnostic.Log(p.Log, rejected!, false);
            string line = p.Log.Captured.Lines.Last();
            Must(line.Contains("stateInvariant=LifetimeLimit") && line.Contains("unit=Ticks") &&
                !line.Contains(template.Request) && !line.Contains(prior.Id));
        });
        foreach (long step in new[] { 1L, TimeSpan.TicksPerMillisecond })
            await check("Private advancing real-clock-shaped ingress persists routes and resumes with exact TTL " + step, async () =>
            {
                await using PrivateHarness p = new();
                PrivateIngress result = await PrivateCoordinatorWithClock(p,
                    new AdvancingPrivateClock(p.H.Clock.GetUtcNow(), step)).Handle(p.Origin, default);
                PrivateJob job = p.Store.List().Single();
                Must(result.Reply == PrivateAnalysisCoordinator.CardSent &&
                    job.Expires - job.Created == TimeSpan.FromMinutes(30) &&
                    job.State == PrivateJobState.AwaitingApproval && job.MessageId is not null &&
                    p.Ui.Wire.Requests.Count == 2);
                await p.Authenticate(await p.Approve(job));
                await p.Worker.RunOne(default);
                Must(p.Store.Get(job.Id).State == PrivateJobState.Completed && p.H.Handler.Mutations == 0);
            });
        await check("Private advancing-clock accepted SDK request round trips DPAPI and renewed private approval", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using PrivateHarness p = new();
            await PrivateCoordinatorWithClock(p, new AdvancingPrivateClock(p.H.Clock.GetUtcNow(), TimeSpan.TicksPerMillisecond))
                .Handle(p.Origin, default);
            PrivateJob original = p.Store.List().Single();
            string directory = Path.Combine(Path.GetTempPath(), "lab13-offline-" + Guid.NewGuid().ToString("N"), "lab13-private-analysis");
            string parent = Path.GetDirectoryName(directory)!;
            try
            {
                using (DpapiPrivateStateStorage disk = new(directory)) disk.Save(p.Storage.Bytes!);
                using PrivateJobStore restored = new(new DpapiPrivateStateStorage(directory), p.H.Clock);
                PrivateJob job = restored.Get(original.Id);
                Must(job.ImmutableKey == original.ImmutableKey && job.Expires - job.Created == TimeSpan.FromMinutes(30) &&
                    job.State == PrivateJobState.AwaitingApproval && job.Revision == original.Revision + 1 &&
                    job.HumanGeneration is null);
                PrivateAuthorizations auth = new(restored, p.Options, p.H.Policy, p.H.Settings, p.H.HumanSettings, p.ModelOptions, p.H.Clock);
                await using HumanConnections humans = new(p.H.HumanSettings, p.H.Cache,
                    new(tokens => new WorkIqSession(tokens, p.H.Handler)), p.H.Clock, auth);
                PrivateAnalysisCoordinator coordinator = new(p.Options, restored, auth, humans, p.H.HumanSettings, p.Teams,
                    new(p.ModelOptions, o => new NaturalLanguageModel(o, p.Routing)), p.H.Policy, p.H.Settings, p.H.Clock,
                    NullLogger<PrivateAnalysisCoordinator>.Instance);
                int before = p.Ui.Wire.Requests.Count;
                await coordinator.Handle(p.Personal, default);
                job = restored.Get(job.Id);
                await coordinator.Action(p.Personal, "lab13.private.approve", job.Id, job.Revision, default);
                Must(restored.Get(job.Id).State == PrivateJobState.AwaitingSignIn &&
                    p.Ui.Wire.Requests.Skip(before).All(r => r.Method == HttpMethod.Put &&
                        r.Identity?.AgenticUserId == Harness.Au &&
                        r.Body.GetProperty("conversation").GetProperty("id").GetString() == "19:personal") &&
                    p.Routing.Requests.Count == 1 && p.Model.Requests.Count == 0);
            }
            finally
            {
                foreach (string file in new[] { "jobs.dpapi", "jobs.pending", "host.lock", "owner.txt" })
                    if (File.Exists(Path.Combine(directory, file))) File.Delete(Path.Combine(directory, file));
                if (Directory.Exists(directory)) Directory.Delete(directory);
                if (Directory.Exists(parent)) Directory.Delete(parent);
            }
        });
        foreach (PrivateStateInvariant invariant in Enum.GetValues<PrivateStateInvariant>())
            await check("Private durable invariant remains fail closed with safe detail " + invariant, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob template = await p.Initiate();
                byte[] before = p.Storage.Bytes!.ToArray();
                PrivateJob value = template with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) };
                value = invariant switch
                {
                    PrivateStateInvariant.RecordIdentity => value with { Owner = "PRIVATE-invalid-human" },
                    PrivateStateInvariant.RequestLength => value with { Request = new string('x', 16001) },
                    PrivateStateInvariant.MetadataLength => value with { Metadata = new string('x', 16001) },
                    PrivateStateInvariant.ContextShape => value with { Origin = Json("PRIVATE-not-an-object") },
                    PrivateStateInvariant.ConfigurationShape => value with { Configuration = "PRIVATE-invalid-config" },
                    PrivateStateInvariant.MessageIdLength => value with { MessageId = new string('x', 2049) },
                    PrivateStateInvariant.LifetimeOrder => value with { Expires = value.Created },
                    PrivateStateInvariant.LifetimeLimit => value with { Expires = value.Created.AddMinutes(30).AddTicks(1) },
                    PrivateStateInvariant.Revision => value with { Revision = 0 },
                    PrivateStateInvariant.State => value with { State = (PrivateJobState)999 },
                    PrivateStateInvariant.ConsentScope => value with { ConsentScope = (PrivateConsentScope)999 },
                    PrivateStateInvariant.ResultLength => value with { Result = new string('x', 24577) },
                    PrivateStateInvariant.RecordBytes => value with { Origin = Json(new { synthetic = new string('x', 128 * 1024) }) },
                    PrivateStateInvariant.ClaimedReadyWithoutResult => value with { HasRun = true, State = PrivateJobState.Ready, Result = null },
                    _ => throw new InvalidOperationException()
                };
                PrivateStateValidationException? rejected = null;
                try { p.Store.Add(value); } catch (PrivateStateValidationException error) { rejected = error; }
                Must(rejected?.Invariant == invariant && before.SequenceEqual(p.Storage.Bytes!) &&
                    p.Store.List().Single().Id == template.Id);
                using PrivateIngressDiagnostic diagnostic = new();
                diagnostic.Log(p.Log, rejected!, false);
                string log = p.Log.Captured.Lines.Last();
                Must(log.Contains("stateInvariant=" + invariant) && !log.Contains("PRIVATE") &&
                    !log.Contains(template.Request) && !log.Contains(template.Owner) && !log.Contains(value.Id));
            });
        await check("Private exact lifetime character and serialized record-byte ceilings remain accepted", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob template = await p.Initiate();
            PrivateJob exact = template with
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), Request = new string('x', 16000),
                Metadata = new string('x', 16000), MessageId = new string('x', 2048), Result = new string('x', 24576),
                Expires = template.Created.AddMinutes(30), Origin = Json(new { synthetic = "" })
            };
            int padding = 128 * 1024 - JsonSerializer.SerializeToUtf8Bytes(exact).Length;
            Must(padding > 0);
            exact = exact with { Origin = Json(new { synthetic = new string('x', padding) }) };
            Must(JsonSerializer.SerializeToUtf8Bytes(exact).Length == 128 * 1024);
            p.Store.Add(exact);
            Must(p.Store.Get(exact.Id).ImmutableKey == exact.ImmutableKey);
            byte[] before = p.Storage.Bytes!.ToArray();
            await Denied(async () =>
            {
                p.Store.Add(exact with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                    Origin = Json(new { synthetic = new string('x', padding + 1) }) });
                await Task.CompletedTask;
            });
            Must(before.SequenceEqual(p.Storage.Bytes!));
        });
    }
}
