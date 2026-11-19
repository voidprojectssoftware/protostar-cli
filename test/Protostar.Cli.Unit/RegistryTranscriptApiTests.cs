using System.Net;
using System.Text;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>How <see cref="RegistryApi"/> reads the registry's refusals of transcript requests.</summary>
public sealed class RegistryTranscriptApiTests
{
    private static readonly Uri Registry = new("https://registry.test");
    private static readonly Guid MachineId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly LocalTranscript Transcript = new(
        "claude-code", "6f83525b-790f-4654-944d-ba3b0c96453e", TranscriptKind.Session, null, "/tmp/session.jsonl", null);

    private const string GenericType = "https://tools.ietf.org/html/rfc9110#section-15.5.1";
    private const string UnknownMachineType = "https://protostar.dev/problems/unknown-machine";
    private const string UnknownTranscriptType = "https://protostar.dev/problems/unknown-transcript";

    // The registry's real answers (TranscriptService): a 400 problem whose title names the machine.
    private static string AppendRefusal(Guid id) => $"Machine {id} is not registered.";
    private static string RegisterRefusal(Guid id) => $"Machine {id} is not registered. Register it with PUT /v1/machines/{{machineId}} first.";

    [Theory]
    [InlineData(UnknownMachineType)]
    [InlineData(GenericType)]
    public async Task An_append_refused_for_an_unknown_machine_is_reported_as_such(string type)
    {
        // A registry from before registry#41 sends a generic type, and only the title tells.
        var api = Api(HttpStatusCode.BadRequest, AppendRefusal(MachineId), type);

        var result = await Append(api);

        Assert.Equal(AppendStatus.UnknownMachine, result.Status);
    }

    [Fact]
    public async Task A_transcript_registration_refused_for_an_unknown_machine_is_reported_as_such()
    {
        var api = Api(HttpStatusCode.BadRequest, RegisterRefusal(MachineId));

        var result = await api.RegisterTranscriptAsync(Registry, "tok", Transcript, MachineId, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.MachineUnknown);
    }

    [Fact]
    public async Task The_unknown_machine_type_is_recognised_whatever_the_title_says()
    {
        var api = Api(HttpStatusCode.BadRequest, "Some reworded title.", UnknownMachineType);

        var appended = await Append(api);
        var registered = await api.RegisterTranscriptAsync(Registry, "tok", Transcript, MachineId, null, CancellationToken.None);

        Assert.Equal(AppendStatus.UnknownMachine, appended.Status);
        Assert.True(registered.MachineUnknown);
    }

    [Fact]
    public async Task An_append_refused_with_the_unknown_transcript_type_is_reported_as_such()
    {
        var api = Api(HttpStatusCode.NotFound, "No transcript with that harness and key.", UnknownTranscriptType);

        var result = await Append(api);

        Assert.Equal(AppendStatus.UnknownTranscript, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, GenericType)]
    [InlineData(HttpStatusCode.BadRequest, UnknownTranscriptType)]
    [InlineData(HttpStatusCode.InternalServerError, UnknownTranscriptType)]
    public async Task Only_a_404_with_the_unknown_transcript_type_means_the_transcript_is_unknown(HttpStatusCode status, string type)
    {
        var api = Api(status, "No transcript with that harness and key.", type);

        var result = await Append(api);

        Assert.NotEqual(AppendStatus.UnknownTranscript, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "The chunk does not match its X-Content-SHA256 header; it was corrupted in transit.")]
    [InlineData(HttpStatusCode.BadRequest, "Machine 99999999-2222-3333-4444-555555555555 is not registered.")]
    [InlineData(HttpStatusCode.NotFound, "Machine 11111111-2222-3333-4444-555555555555 is not registered.")]
    public async Task Other_refusals_are_not_mistaken_for_an_unknown_machine(HttpStatusCode status, string title)
    {
        var api = Api(status, title);

        var appended = await Append(api);
        var registered = await api.RegisterTranscriptAsync(Registry, "tok", Transcript, MachineId, null, CancellationToken.None);

        Assert.Equal(AppendStatus.Rejected, appended.Status);
        Assert.False(registered.MachineUnknown);
    }

    [Fact]
    public async Task A_registry_without_the_lookup_says_to_update_it_rather_than_a_bare_404()
    {
        // A registry from before the endpoint has no route for it: a 404 with no problem body.
        var api = new RegistryApi(new StatusHandler(HttpStatusCode.NotFound));

        var result = await api.LookupAsync(Registry, "tok", [Transcript], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Update the registry", result.Error);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static Task<AppendResult> Append(RegistryApi api) =>
        api.AppendAsync(Registry, "tok", Transcript.Harness, Transcript.Key, 1, 0, MachineId, Encoding.UTF8.GetBytes("{}\n"), CancellationToken.None);

    private static RegistryApi Api(HttpStatusCode status, string title, string type = GenericType) => new(new ProblemHandler(status, title, type));

    private sealed class ProblemHandler(HttpStatusCode status, string title, string type) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    $$"""{"type":"{{type}}","title":"{{title}}","status":{{(int)status}}}""",
                    Encoding.UTF8,
                    "application/problem+json"),
            });
    }
}
