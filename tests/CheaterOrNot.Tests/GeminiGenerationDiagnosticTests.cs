using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class GeminiGenerationDiagnosticTests
{
    private const string Key = "AQ.synthetic_Diagnostic_Key_1234567890";
    private const string Ready = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"READY"}]}}]}""";
    private static readonly GeminiGenerationDiagnostic Diagnostic = new(GeminiReviewRequest.DefaultModel);

    [Theory]
    [InlineData(Ready, 1)]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"REA"},{"text":"DY"}]}}]}""", 2)]
    public async Task DiagnosticChecksMetadataThenSendsOnlyTheExactTinyPrompt(string responseBody, int expectedParts)
    {
        var calls = new List<HttpMethod>();
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls.Add(request.Method);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(Diagnostic.MetadataEndpoint, request.RequestUri);
                Assert.Null(request.Content);
                return Metadata();
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Diagnostic.GenerationEndpoint, request.RequestUri);
            Assert.Equal(GeminiGenerationDiagnostic.JsonBody, await request.Content!.ReadAsStringAsync(token));
            Assert.DoesNotContain("inlineData", GeminiGenerationDiagnostic.JsonBody);
            Assert.DoesNotContain("responseJsonSchema", GeminiGenerationDiagnostic.JsonBody);
            return Response(HttpStatusCode.OK, responseBody);
        }));
        var result = await client.DiagnoseGenerationAsync(Diagnostic, Key, CancellationToken.None);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], calls);
        Assert.True(result.Passed);
        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.Equal(200, step.HttpStatus));
        Assert.Equal(expectedParts, result.Steps[1].ResponseParts);
        Assert.Equal(5, result.Steps[1].ResponseTextCharacters);
        Assert.DoesNotContain(Key, result.ToJson());
        Assert.Contains("does not validate video", result.Interpretation);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task MetadataFailureStopsBeforeGenerationWithoutSavingProviderBody(int status)
    {
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Response((HttpStatusCode)status, Key + " PRIVATE_PROVIDER_BODY"));
        }));
        var result = await client.DiagnoseGenerationAsync(Diagnostic, Key, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(result.Passed);
        Assert.Equal(status, Assert.Single(result.Steps).HttpStatus);
        Assert.Contains("generation was not attempted", result.Interpretation);
        Assert.DoesNotContain(Key, result.ToJson());
        Assert.DoesNotContain("PRIVATE_PROVIDER_BODY", result.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation503KeepsSafeHeadersAndNeverRetriesOrLeaksHtml(bool structured)
    {
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((request, _) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return Task.FromResult(Metadata());
            var response = Response(HttpStatusCode.ServiceUnavailable, Key + " PRIVATE_HTML_BODY");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }));
        var result = await client.DiagnoseGenerationAsync(new(Diagnostic.Model, structured), Key, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.False(result.Passed);
        Assert.True(result.Steps[0].Passed);
        var generation = result.Steps[1];
        Assert.Equal(503, generation.HttpStatus);
        Assert.Equal("text/html", generation.ResponseMediaType);
        Assert.Equal(30d, generation.RetryAfterSeconds);
        Assert.Contains("HTTP 503", generation.Outcome);
        Assert.DoesNotContain(Key, result.ToJson());
        Assert.DoesNotContain("PRIVATE_HTML_BODY", result.ToJson());
    }

    [Theory]
    [InlineData("""{"name":"models/wrong","supportedGenerationMethods":["generateContent"]}""")]
    [InlineData("""{"name":"models/gemini-3.1-flash-lite","supportedGenerationMethods":[]}""")]
    public async Task UnconfirmedMetadataIsNotTreatedAsGenerationAccess(string body)
    {
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(HttpStatusCode.OK, body));
        }));
        var result = await client.DiagnoseGenerationAsync(Diagnostic, Key, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(Assert.Single(result.Steps).Passed);
    }

    [Theory]
    [InlineData("""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"READY"}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"PRIVATE_UNEXPECTED_OUTPUT"}]}}]}""")]
    [InlineData("malformed private body")]
    public async Task Http200AloneDoesNotPassAndRawOutputIsNeverSaved(string body)
    {
        using var client = new GeminiReviewClient(new Handler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get ? Metadata() : Response(HttpStatusCode.OK, body))));
        var result = await client.DiagnoseGenerationAsync(Diagnostic, Key, CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Equal(200, result.Steps[1].HttpStatus);
        Assert.Contains("HTTP 200", result.Interpretation);
        Assert.DoesNotContain("PRIVATE_UNEXPECTED_OUTPUT", result.ToJson());
        Assert.DoesNotContain("malformed private body", result.ToJson());
    }

    [Fact]
    public async Task CancellationIsNotTurnedIntoADiagnosticFailureOrRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((_, token) =>
        {
            calls++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Metadata());
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DiagnoseGenerationAsync(Diagnostic, Key, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("""{"status":"READY"}""")]
    [InlineData("""{ "status" : "READY" }""")]
    public async Task StructuredModeSendsOnlyItsPreviewedJsonContractAndAcceptsMatchingOutput(string output)
    {
        var diagnostic = new GeminiGenerationDiagnostic(Diagnostic.Model, structuredOutput: true);
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            if (request.Method == HttpMethod.Get) return Metadata();
            Assert.Equal(diagnostic.GenerationEndpoint, request.RequestUri);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Equal(diagnostic.RequestJson, body);
            Assert.Contains(body, diagnostic.Preview);
            Assert.NotEqual(Diagnostic.RequestSha256, diagnostic.RequestSha256);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), diagnostic.RequestBytes);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(["contents", "generationConfig"], json.RootElement.EnumerateObject().Select(p => p.Name));
            var config = json.RootElement.GetProperty("generationConfig");
            Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
            var schema = config.GetProperty("responseJsonSchema");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("status", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.Equal("READY", Assert.Single(schema.GetProperty("properties").GetProperty("status")
                .GetProperty("enum").EnumerateArray()).GetString());
            return Response(HttpStatusCode.OK, Generation(output));
        }));
        var result = await client.DiagnoseGenerationAsync(diagnostic, Key, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.True(result.Passed);
        Assert.True(result.StructuredOutput);
        Assert.Equal("structured_text_generation", result.Steps[1].Check);
        Assert.Contains("larger review schema", result.Interpretation);
    }

    [Theory]
    [InlineData("READY")]
    [InlineData("```json\n{\"status\":\"READY\"}\n```")]
    [InlineData("""{"status":"PRIVATE_WRONG_VALUE"}""")]
    [InlineData("""{"status":true}""")]
    [InlineData("""{"status":"READY","private":"PRIVATE_EXTRA_FIELD"}""")]
    [InlineData("""{"status":"READY","status":"READY"}""")]
    [InlineData("""[{"status":"READY"}]""")]
    [InlineData("""{}""")]
    public async Task StructuredModeRejectsWrongContractsWithoutSavingGeneratedText(string output)
    {
        using var client = new GeminiReviewClient(new Handler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get ? Metadata() : Response(HttpStatusCode.OK, Generation(output)))));
        var result = await client.DiagnoseGenerationAsync(new(Diagnostic.Model, true), Key, CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Equal(200, result.Steps[1].HttpStatus);
        Assert.Contains("HTTP 200", result.Interpretation);
        Assert.DoesNotContain("PRIVATE_WRONG_VALUE", result.ToJson());
        Assert.DoesNotContain("PRIVATE_EXTRA_FIELD", result.ToJson());
        Assert.Equal(output.Length, result.Steps[1].ResponseTextCharacters);
    }

    [Fact]
    public async Task StructuredModeNeverPostsWhenMetadataDoesNotConfirmTheModel()
    {
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Response(HttpStatusCode.OK,
                """{"name":"models/wrong","supportedGenerationMethods":["generateContent"]}"""));
        }));
        var result = await client.DiagnoseGenerationAsync(new(Diagnostic.Model, true), Key, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(result.Passed);
        Assert.Contains("generation was not attempted", result.Interpretation);
    }

    private static string Generation(string text) => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } }
    });

    private static HttpResponseMessage Metadata() => Response(HttpStatusCode.OK,
        $$"""{"name":"models/{{Diagnostic.Model}}","supportedGenerationMethods":["generateContent"]}""");

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
