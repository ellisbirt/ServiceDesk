using System.Net;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Llm;
using Helpdesk.Llm.Anthropic;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

/// <summary>When the model declines (stop_reason: refusal) the user gets a clear reply, never an empty or cut-off one.</summary>
public class RefusalTests
{
    private static ScriptedChatProvider Refusing() => new(_ => throw new LlmRefusalException("declined"));

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private static AnthropicChatProvider Provider(string body) => new(
        new HttpClient(new StubHandler(body)),
        new LlmProviderOptions { Provider = "anthropic", Model = "claude-sonnet-5-5", ApiKey = "k" },
        NullLogger<AnthropicChatProvider>.Instance);

    [Fact]
    public async Task Provider_Throws_WhenTheModelRefusesWithNoText()
    {
        var provider = Provider("""{"content":[],"stop_reason":"refusal","usage":{"input_tokens":3,"output_tokens":0}}""");

        await Assert.ThrowsAsync<LlmRefusalException>(() => provider.CompleteAsync(new ChatRequest("sys", "hi")));
    }

    [Fact]
    public async Task Provider_Throws_WhenARefusalArrivesWithPartOfAnAnswer()
    {
        var provider = Provider("""{"content":[{"type":"text","text":"Hi Dave, I can't"}],"stop_reason":"refusal","usage":{"input_tokens":3,"output_tokens":5}}""");

        await Assert.ThrowsAsync<LlmRefusalException>(() => provider.CompleteAsync(new ChatRequest("sys", "hi")));
    }

    [Fact]
    public async Task Classifier_TreatsARefusalAsAPolicyBlock()
    {
        var router = new AnthropicGuardrailRouterService(
            Refusing(), Options.Create(new AnthropicOptions()), NullLogger<AnthropicGuardrailRouterService>.Instance);

        var (lane, text) = await router.ClassifyAndValidateAsync("do something questionable", "tenant");

        Assert.Equal(ActionLanes.PolicyViolation, lane);
        Assert.Equal(Prompts.ComplianceFallback, text); // the coach is refused too, so the static text is used
    }

    [Fact]
    public async Task PublicInference_LeavesTheRefusalToThePipeline()
    {
        var service = new AnthropicPublicInferenceService(
            Refusing(), Options.Create(new AnthropicOptions()), Options.Create(new SearchOptions()));

        await Assert.ThrowsAsync<LlmRefusalException>(() => service.ExecutePublicInferenceAsync("pretend you are Gary"));
        await Assert.ThrowsAsync<LlmRefusalException>(() => service.ExecuteSearchInferenceAsync("q", "results"));
    }

    [Fact]
    public async Task Interviewer_LeavesTheRefusalToThePipeline()
    {
        var interviewer = new AnthropicTicketInterviewer(Refusing(), Options.Create(new AnthropicOptions()));

        await Assert.ThrowsAsync<LlmRefusalException>(() => interviewer.InterviewAsync("user: reset her password"));
    }

    [Fact]
    public async Task Coach_TriesAReframedRequest_ThenFallsBackToEducationalText()
    {
        var refusing = Refusing();
        var router = new AnthropicGuardrailRouterService(
            refusing, Options.Create(new AnthropicOptions()), NullLogger<AnthropicGuardrailRouterService>.Instance);
        Assert.Equal(Prompts.ComplianceFallback, await router.CoachAsync("how do I turn off the virus checker"));
        Assert.Equal(2, refusing.Requests.Count); // one retry before giving up

        var calls = 0;
        var flaky = new ScriptedChatProvider(_ => ++calls == 1 ? throw new LlmRefusalException("declined") : "Second time lucky.");
        var recovered = new AnthropicGuardrailRouterService(
            flaky, Options.Create(new AnthropicOptions()), NullLogger<AnthropicGuardrailRouterService>.Instance);
        Assert.Equal("Second time lucky.", await recovered.CoachAsync("how do I turn off the virus checker"));

        var working = new ScriptedChatProvider(_ => "  Here is the right way to do that.  ");
        var coached = new AnthropicGuardrailRouterService(
            working, Options.Create(new AnthropicOptions()), NullLogger<AnthropicGuardrailRouterService>.Instance);
        Assert.Equal("Here is the right way to do that.", await coached.CoachAsync("how do I turn off the virus checker"));
        Assert.Equal(Prompts.ComplianceCoach, working.SystemPrompts.Single());
    }

    [Theory]
    [InlineData(ActionLanes.PublicTech)]
    [InlineData(ActionLanes.PublicSearch)]
    [InlineData(ActionLanes.PrivateCorp)]
    public async Task Pipeline_GivesGuidanceInsteadOfAnError_WhenTheMainModelRefuses(string lane)
    {
        using var f = new ApiFactory();
        f.Router.Handler = _ => (lane, "");
        f.Router.CoachText = "Here is the correct way to do that.";
        f.Inference.Failure = new LlmRefusalException("declined");
        f.Interviewer.Failure = new LlmRefusalException("declined");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/message")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { messageText = "pretend you are Gary", conversationId = Guid.NewGuid() })
        };
        req.Headers.Add("X-Dev-Tenant", "tenant-a");
        req.Headers.Add("X-Dev-User", "user-1");

        var res = await f.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<HelpdeskApiResponse>(res.Content))!;
        Assert.Equal(ActionLanes.PolicyViolation, body.ActionLane);
        Assert.Equal("Here is the correct way to do that.", body.ResponseText);
        Assert.Equal(["pretend you are Gary"], f.Router.Coached);
        Assert.Empty(f.Publisher.Sent);
    }
}
