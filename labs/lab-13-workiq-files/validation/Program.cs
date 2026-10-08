using WorkIqFiles;

int passed = 0;
List<string> failures = [];
async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error)
    {
        string failure = name + ": " + error;
        failures.Add(failure);
        Console.WriteLine("FAIL " + failure);
    }
}

string suite = args.Length == 0 ? "all-current" : args is [var selector] ? selector :
    throw new ArgumentException("Expected one documented suite selector.");
Console.WriteLine("RETIRED: WorkIQ deterministic slash file/setup/share/Section2/confirmation recipes and guarded broker suites. This publication runs native WorkIQ/auth/privacy coverage only; sibling-provider suites are not included. Counts are not comparable to the former historical aggregate; see TESTING.md.");
switch (suite)
{
    case "--provider-isolation":
        await WorkIqPublicationChecks.Run(Check); break;
    case "--workiq-skill":
    case "--full-workiq-guide":
        await NaturalLanguageChecks.FullWorkIqGuideChecks(Check); break;
    case "--workiq-ingress":
        await NaturalLanguageChecks.WorkIqIngressChecks(Check);
        await NativeSessionChecks.Run(Check);
        await HumanChecks.Run(Check); break;
    case "all-current":
        await NaturalLanguageChecks.Run(Check);
        await NaturalLanguageChecks.WorkIqIngressChecks(Check);
        await NativeSessionChecks.Run(Check);
        await HumanChecks.Run(Check);
        await WorkIqPublicationChecks.Run(Check);
        break;
    default: throw new ArgumentException("Unknown suite selector: " + suite);
}
if (failures.Count > 0) throw new Exception($"{failures.Count} {suite} checks failed; {passed} passed. See FAIL entries.");
Console.WriteLine($"PASS: {passed} {suite} synthetic checks; no live calls.");

internal sealed class RedirectHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response = new(System.Net.HttpStatusCode.Redirect);
        response.Headers.Location = new Uri("https://never-follow.invalid/secret");
        return Task.FromResult(response);
    }
}
