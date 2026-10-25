using Helpdesk.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Tests;

/// <summary>Which model vendor the intake chat resolves to for a given configuration.</summary>
public class ProviderSelectionTests
{
    private static IChatProvider ChatProvider(IReadOnlyDictionary<string, string> settings)
    {
        var f = new ApiFactory(settings: settings);
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IChatProvider>();
    }

    [Fact]
    public void ByDefault_AnAnthropicKeyGivesAnthropic()
    {
        var chat = ChatProvider(new Dictionary<string, string> { ["Anthropic:ApiKey"] = "test-key" });

        Assert.Equal(LlmProviders.Anthropic, chat.Provider);
        Assert.Equal("claude-sonnet-5-5", chat.Model);
    }

    [Fact]
    public void TheAnthropicModelSettingStillSelectsTheModel()
    {
        var chat = ChatProvider(new Dictionary<string, string>
        {
            ["Anthropic:ApiKey"] = "test-key",
            ["Anthropic:Model"] = "claude-haiku-5-5"
        });

        Assert.Equal(LlmProviders.Anthropic, chat.Provider);
        Assert.Equal("claude-haiku-5-5", chat.Model);
    }

    [Theory]
    [InlineData("openai", "gpt-test", "openai")]
    [InlineData("azure_openai", "my-deployment", "azure_openai")]
    [InlineData("microsoft", "my-deployment", "azure_openai")]
    [InlineData("grok", "grok-test", "grok")]
    [InlineData("ollama", "llama-test", "ollama")]
    [InlineData("lm_studio", "local-test", "lm_studio")]
    public void AnExplicitChatRole_SwitchesVendor_WithoutAnAnthropicKey(string configured, string model, string expected)
    {
        var chat = ChatProvider(new Dictionary<string, string>
        {
            ["Llm:Roles:chat:Provider"] = configured,
            ["Llm:Roles:chat:Model"] = model,
            ["Llm:Roles:chat:ApiKey"] = "test-key",
            ["Llm:Roles:chat:Endpoint"] = "https://example.invalid/"
        });

        Assert.Equal(expected, chat.Provider);
        Assert.Equal(model, chat.Model);
    }

    [Fact]
    public void AnExplicitChatRole_WinsOverTheAnthropicSection()
    {
        var chat = ChatProvider(new Dictionary<string, string>
        {
            ["Anthropic:ApiKey"] = "test-key",
            ["Llm:Roles:chat:Provider"] = "openai",
            ["Llm:Roles:chat:Model"] = "gpt-test",
            ["Llm:Roles:chat:ApiKey"] = "other-key"
        });

        Assert.Equal(LlmProviders.OpenAI, chat.Provider);
    }

    [Fact]
    public void AnExplicitAnthropicRole_IsAlsoAccepted()
    {
        var chat = ChatProvider(new Dictionary<string, string>
        {
            ["Llm:Roles:chat:Provider"] = "claude",
            ["Llm:Roles:chat:Model"] = "claude-opus-5-5",
            ["Llm:Roles:chat:ApiKey"] = "test-key"
        });

        Assert.Equal(LlmProviders.Anthropic, chat.Provider);
        Assert.Equal("claude-opus-5-5", chat.Model);
    }

    [Fact]
    public void AnUnknownVendor_IsRefusedWhenUsed()
    {
        var settings = new Dictionary<string, string>
        {
            ["Llm:Roles:chat:Provider"] = "not-a-vendor",
            ["Llm:Roles:chat:Model"] = "x",
            ["Llm:Roles:chat:ApiKey"] = "test-key"
        };

        Assert.Throws<InvalidOperationException>(() => ChatProvider(settings).Provider);
    }
}
