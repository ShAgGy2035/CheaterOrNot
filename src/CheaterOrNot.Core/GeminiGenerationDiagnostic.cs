using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CheaterOrNot.Core;

public sealed class GeminiGenerationDiagnostic
{
    public const string JsonBody = """
        {"contents":[{"role":"user","parts":[{"text":"Reply with exactly READY."}]}],"generationConfig":{"temperature":0,"maxOutputTokens":256}}
        """;
    public const string StructuredJsonBody = """
        {"contents":[{"role":"user","parts":[{"text":"Return a JSON object with exactly one property named status whose value is the string READY."}]}],"generationConfig":{"temperature":0,"maxOutputTokens":256,"responseMimeType":"application/json","responseJsonSchema":{"type":"object","properties":{"status":{"type":"string","enum":["READY"]}},"required":["status"],"additionalProperties":false}}}
        """;
    public string Model { get; }
    public bool StructuredOutput { get; }
    public string RequestJson => StructuredOutput ? StructuredJsonBody : JsonBody;
    public Uri MetadataEndpoint => new($"https://generativelanguage.googleapis.com/v1beta/models/{Model}");
    public Uri GenerationEndpoint => new($"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent");
    public string RequestSha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RequestJson)));
    public int RequestBytes => Encoding.UTF8.GetByteCount(RequestJson);

    public GeminiGenerationDiagnostic(string model, bool structuredOutput = false)
    {
        Model = GeminiReviewRequest.ValidateModel(model);
        StructuredOutput = structuredOutput;
    }

    public string Preview => $"{(StructuredOutput ? "STRUCTURED TEXT" : "MINIMAL")} GENERATION DIAGNOSTIC - NOT RUN\n" +
        $"1. GET {MetadataEndpoint} (model metadata, no prompt).\n" +
        $"2. Only if model access is confirmed: POST {GenerationEndpoint}\n" +
        $"Exactly one generation attempt, no retry or fallback. Each step has a 60-second deadline.\n" +
        $"The saved key is used privately for authentication, never included here or in the report.\n" +
        $"No demo, footage, names, IDs, gameplay or files are sent.\n" +
        (StructuredOutput
            ? "JSON structured-output schema is sent. Expected response: exactly one status property with string value READY.\n"
            : "No structured-output schema is sent. Expected response: exactly READY.\n") +
        $"Google data-use/retention terms, quotas and possible charges apply.\n" +
        $"Request: {RequestBytes} UTF-8 bytes; SHA256 {RequestSha256}\n\n{RequestJson}";

    internal void ValidateResponse(string text)
    {
        if (!StructuredOutput)
        {
            if (text != "READY")
                throw new FormatException("Generation returned unexpected text; response text was not saved.");
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1 &&
                root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                status.GetString() == "READY")
                return;
        }
        catch (JsonException)
        {
            throw new FormatException("Generated text was not valid diagnostic JSON; response text was not saved.");
        }
        throw new FormatException("Generated JSON did not match the exact diagnostic contract; response text was not saved.");
    }
}

public sealed record GeminiDiagnosticStep(string Check, string Method, string Endpoint, int? HttpStatus,
    string ResponseMediaType, double? RetryAfterSeconds, bool Passed, string Outcome,
    int? ResponseParts = null, int? ResponseTextCharacters = null);

public sealed record GeminiGenerationDiagnosticResult(string Schema, string Model, DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc, string RequestSha256, int RequestBytes, IReadOnlyList<GeminiDiagnosticStep> Steps,
    bool StructuredOutput = false)
{
    public bool Passed => Steps.Count == 2 && Steps.All(step => step.Passed);
    public string Interpretation => !Steps[0].Passed
        ? "Model access was not confirmed; generation was not attempted. This is not a video feasibility result."
        : Passed
            ? StructuredOutput
                ? "The tiny structured-text contract passed at this time. This does not validate video or the larger review schema, factual accuracy, or the cause of earlier failures."
                : "Minimal text generation worked at this time. This does not validate video/structured review or establish the cause of earlier failures."
            : Steps[1].HttpStatus == 503
                ? "The text-only diagnostic returned HTTP 503. The observed failure is not confined to footage or a large request. This does not establish a global outage or permanent infeasibility."
                : Steps[1].HttpStatus == 200
                    ? "Generation returned HTTP 200, but the expected complete diagnostic output was not accepted. The earlier HTTP 503 was not reproduced in this text-only request. This does not validate video review or explain the earlier failures."
                : "Text-only generation did not pass. Use the explicit step outcome; no video feasibility or successful review finding was established.";

    public string ToJson() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
}
