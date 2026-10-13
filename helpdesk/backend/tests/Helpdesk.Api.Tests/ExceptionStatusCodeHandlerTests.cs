using System.Text.Json;
using Helpdesk.Api;
using Helpdesk.Api.Chat;
using Helpdesk.Api.Observability;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Api.Tests;

public class ExceptionStatusCodeHandlerTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("LocalDevelopment", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public async Task Exceptions_ReturnRfc9457ProblemDetails_WithEnvironmentGatedStackTrace(
        string environmentName, bool includesStackTrace)
    {
        var environment = new TestWebHostEnvironment(environmentName);
        var handler = new ExceptionStatusCodeHandler(
            _ => throw new GuardrailClassificationException("private diagnostic detail"),
            NullLogger<ExceptionStatusCodeHandler>.Instance,
            environment);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/chat/message";
        context.Response.Body = new MemoryStream();
        context.Items["CorrelationId"] = "test-correlation";

        await handler.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var problem = document.RootElement;

        Assert.Equal("urn:helpdesk:problem:classification_failed", problem.GetProperty("type").GetString());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.GetProperty("status").GetInt32());
        Assert.Equal("Message could not be safely classified. Please try again.", problem.GetProperty("title").GetString());
        Assert.Equal("/api/v1/chat/message", problem.GetProperty("instance").GetString());
        Assert.Equal("classification_failed", problem.GetProperty("code").GetString());
        Assert.Equal("test-correlation", problem.GetProperty("correlationId").GetString());
        Assert.Equal(includesStackTrace, problem.TryGetProperty("detail", out var detail));
        if (includesStackTrace)
        {
            Assert.Contains("GuardrailClassificationException", detail.GetString());
            Assert.Contains("private diagnostic detail", detail.GetString());
            Assert.Contains(" at ", detail.GetString());
        }
    }

    [Fact]
    public async Task RateLimitedException_PreservesRetryAfterHeader()
    {
        var handler = new ExceptionStatusCodeHandler(
            _ => throw new UpstreamRateLimitedException(TimeSpan.FromSeconds(3.2), "rate limited"),
            NullLogger<ExceptionStatusCodeHandler>.Instance,
            new TestWebHostEnvironment("Production"));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await handler.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("4", context.Response.Headers.RetryAfter);
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Helpdesk.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
