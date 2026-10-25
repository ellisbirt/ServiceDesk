using System.Net;
using Azure;
using Azure.Core;
using Helpdesk.Api;
using Helpdesk.Api.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Api.Tests;

public class QueueRetryTests
{
    [Theory]
    [InlineData("Retry-After", "12", 12)]
    [InlineData("Retry-After", "0", 0)]
    [InlineData("x-ms-retry-after-ms", "2500", 2.5)]
    public void StorageRetryDelay_IsReadFromResponse(string header, string value, double expected)
    {
        var exception = Failure(header, value);
        Assert.Equal(expected, ApiErrors.RetryAfterSeconds(exception));
        Assert.Equal(expected, ApiErrors.RetryAfterSeconds(new AggregateException(exception)));
        Assert.Equal("queue_unavailable", ApiErrors.Map(exception).Code);
    }

    [Theory]
    [InlineData("Retry-After", "-1")]
    [InlineData("Retry-After", "unknown")]
    [InlineData("x-ms-retry-after-ms", "NaN")]
    [InlineData("x-ms-retry-after-ms", "-100")]
    public void InvalidDelay_IsUnknown(string header, string value) =>
        Assert.Null(ApiErrors.RetryAfterSeconds(Failure(header, value)));

    [Fact]
    public void HttpDateDelay_IsRead()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(30).ToString("r");
        Assert.InRange(ApiErrors.RetryAfterSeconds(Failure("Retry-After", date))!.Value, 28, 30);
        Assert.Equal(0, ApiErrors.RetryAfterSeconds(Failure("Retry-After", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("r"))));
    }

    [Fact]
    public void NoStorageResponse_MeansUnknownDelay() =>
        Assert.Null(ApiErrors.RetryAfterSeconds(new RequestFailedException("queue down")));

    [Fact]
    public async Task HttpFailure_ExposesRoundedRetryAfter()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new ExceptionStatusCodeHandler(
            _ => throw Failure("x-ms-retry-after-ms", "2500"),
            NullLogger<ExceptionStatusCodeHandler>.Instance, new TestEnvironment());

        await handler.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("3", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task HubFailure_IncludesRetryDelay()
    {
        await using var rig = new ProcessorRig();
        rig.Inference.OnReply = (_, _) => throw Failure("Retry-After", "12");
        await rig.Processor.StartAsync(CancellationToken.None);
        var job = rig.NewJob("question");

        await ProcessorRig.Eventually(() => rig.Hub.For("failed").Any(e => e.RequestId == job.RequestId), "failure event");

        var failed = Assert.Single(rig.Hub.For("failed"));
        Assert.Equal("queue_unavailable", failed.ErrorCode);
        Assert.Equal(12, failed.RetryAfterSeconds);
        await rig.Processor.StopAsync(CancellationToken.None);
    }

    private static RequestFailedException Failure(string header, string value) =>
        new(new StorageResponse(header, value));

    private sealed class StorageResponse(string header, string value) : Response
    {
        public override int Status => 503;
        public override string ReasonPhrase => "Service Unavailable";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => name.Equals(header, StringComparison.OrdinalIgnoreCase);
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [new(header, value)];
        protected override bool TryGetHeader(string name, out string result)
        {
            result = ContainsHeader(name) ? value : "";
            return ContainsHeader(name);
        }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = ContainsHeader(name) ? [value] : [];
            return ContainsHeader(name);
        }
    }

    private sealed class TestEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
