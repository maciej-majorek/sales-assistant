using System.Net.Http.Headers;
using System.Text.Json;
using SalesAssistant;

var builder = WebApplication.CreateBuilder(args);

// Enable CORS for cross-origin requests from different backend ports
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

// DIAL platform configuration
const string DialUrl = "https://ai-proxy.lab.epam.com";
const string DialDeployment = "gpt-4";
const string DialApiVersion = "2024-02-01";
var dialApiKey = Environment.GetEnvironmentVariable("DIAL_API_KEY") ?? "";

const string SystemPrompt =
    "You are a helpful sales assistant. " +
    "Use the provided tools to query sales data from sales.csv and answer user questions accurately. " +
    "Always base your answers on real data retrieved via tools. " +
    "When presenting numbers, format them clearly (e.g. use $ for currency, commas for thousands). " +
    "If a question is not related to sales data, politely let the user know.";

var staticPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "static");

// Serve static files
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticPath),
    RequestPath = "/static"
});

// Root endpoint - serve index.html
app.MapGet("/", () => Results.File(
    Path.Combine(staticPath, "index.html"),
    "text/html"
));

// Chat endpoint
app.MapPost("/chat", async (ChatRequest req) =>
{
    if (string.IsNullOrEmpty(dialApiKey))
    {
        return Results.Problem("DIAL_API_KEY environment variable not set", statusCode: 500);
    }

    using var httpClient = new HttpClient();
    httpClient.BaseAddress = new Uri(DialUrl);
    httpClient.DefaultRequestHeaders.Add("Api-Key", dialApiKey);

    var tools = DataTools.GetCsvTools();
    var openAiTools = ConvertToolsToOpenAiFormat(tools);

    // Build messages in OpenAI format with system message first
    var messages = new List<object>();
    messages.Add(new { role = "system", content = SystemPrompt });

    // Add history if present
    if (req.History != null)
    {
        foreach (var item in req.History)
        {
            messages.Add(new { role = item.Role.ToLower(), content = item.Content });
        }
    }

    // Add current user message
    messages.Add(new { role = "user", content = req.Message });

    try
    {
        var requestBody = new
        {
            model = DialDeployment,
            max_tokens = 1024,
            tools = openAiTools,
            messages
        };

        var response = await SendDialRequest(httpClient, requestBody);
        var assistantMessage = response.GetProperty("choices")[0].GetProperty("message");

        // Agentic tool-use loop: keep calling until model stops requesting tools
        while (assistantMessage.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
        {
            // Add assistant message with tool calls to history
            messages.Add(assistantMessage);

            // Process each tool call and add results
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var function = toolCall.GetProperty("function");
                var toolName = function.GetProperty("name").GetString()!;
                var toolCallId = toolCall.GetProperty("id").GetString()!;
                var arguments = function.GetProperty("arguments").GetString();

                JsonElement? inputs = null;
                if (!string.IsNullOrEmpty(arguments))
                {
                    inputs = JsonDocument.Parse(arguments).RootElement;
                }

                var result = DataTools.RunTool(toolName, inputs);
                var resultJson = JsonSerializer.Serialize(result);

                // Add tool result message
                messages.Add(new
                {
                    role = "tool",
                    tool_call_id = toolCallId,
                    content = resultJson
                });
            }

            // Call API again with tool results
            var nextRequestBody = new
            {
                model = DialDeployment,
                max_tokens = 1024,
                tools = openAiTools,
                messages
            };

            response = await SendDialRequest(httpClient, nextRequestBody);
            assistantMessage = response.GetProperty("choices")[0].GetProperty("message");
        }

        // Extract the final text reply
        var answer = assistantMessage.TryGetProperty("content", out var contentProp) && contentProp.ValueKind != JsonValueKind.Null
            ? contentProp.GetString() ?? "Sorry, I could not generate a response."
            : "Sorry, I could not generate a response.";

        messages.Add(new { role = "assistant", content = answer });

        // Return history without system message for client storage
        var history = messages.Skip(1).Select(m =>
        {
            var element = JsonSerializer.SerializeToElement(m);
            return new MessageItem(
                element.GetProperty("role").GetString()!,
                element.GetProperty("content")
            );
        }).ToList();

        return Results.Ok(new ChatResponse(answer, history));
    }
    catch (HttpRequestException ex)
    {
        return ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                Results.Problem($"Authentication failed: {ex.Message}", statusCode: 401),
            System.Net.HttpStatusCode.TooManyRequests =>
                Results.Problem($"Rate limit exceeded: {ex.Message}", statusCode: 429),
            _ => Results.Problem($"API error: {ex.Message}", statusCode: 500)
        };
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error: {ex.Message}", statusCode: 500);
    }
});

List<object> ConvertToolsToOpenAiFormat(List<object> anthropicTools)
{
    var openAiTools = new List<object>();
    foreach (var tool in anthropicTools)
    {
        var element = JsonSerializer.SerializeToElement(tool);
        openAiTools.Add(new
        {
            type = "function",
            function = new
            {
                name = element.GetProperty("name").GetString(),
                description = element.GetProperty("description").GetString(),
                parameters = element.GetProperty("input_schema")
            }
        });
    }
    return openAiTools;
}

async Task<JsonElement> SendDialRequest(HttpClient client, object body)
{
    var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    var content = new StringContent(json);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    var endpoint = $"/openai/deployments/{DialDeployment}/chat/completions?api-version={DialApiVersion}";
    var response = await client.PostAsync(endpoint, content);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"API returned {response.StatusCode}: {responseBody}", null, response.StatusCode);
    }

    return JsonDocument.Parse(responseBody).RootElement;
}

app.Run();
