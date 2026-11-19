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

    // The registry's real answers (TranscriptService): a 400 problem whose title names the machine.
    private static string AppendRefusal(Guid id) => $"Machine {id} is not registered.";
    private static string RegisterRefusal(Guid id) => $"Machine {id} is not registered. Register it with PUT /v1/machines/{{machineId}} first.";

    [Fact]
    public async Task An_append_refused_for_an_unknown_machine_is_reported_as_such()
    {
        var api = Api(HttpStatusCode.BadRequest, AppendRefusal(MachineId));

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

    private static Task<AppendResult> Append(RegistryApi api) =>
        api.AppendAsync(Registry, "tok", Transcript.Harness, Transcript.Key, 1, 0, MachineId, Encoding.UTF8.GetBytes("{}\n"), CancellationToken.None);

    private static RegistryApi Api(HttpStatusCode status, string title) => new(new ProblemHandler(status, title));

    private sealed class ProblemHandler(HttpStatusCode status, string title) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    $$"""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"{{title}}","status":{{(int)status}}}""",
                    Encoding.UTF8,
                    "application/problem+json"),
            });
    }
}
