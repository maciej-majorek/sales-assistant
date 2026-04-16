using System.Net.Http.Headers;
using System.Text.Json;
using SalesAssistant;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

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
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrEmpty(apiKey))
    {
        return Results.Problem("ANTHROPIC_API_KEY environment variable not set", statusCode: 500);
    }

    using var httpClient = new HttpClient();
    httpClient.BaseAddress = new Uri("https://api.anthropic.com/");
    httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
    httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

    var tools = DataTools.GetCsvTools();
    var messages = new List<object>();

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
            model = "claude-sonnet-4-6",
            max_tokens = 1024,
            system = SystemPrompt,
            tools,
            messages
        };

        var response = await SendAnthropicRequest(httpClient, requestBody);

        // Agentic tool-use loop: keep calling until Claude stops requesting tools
        while (response.GetProperty("stop_reason").GetString() == "tool_use")
        {
            var toolResults = new List<object>();
            var assistantContent = new List<object>();

            foreach (var content in response.GetProperty("content").EnumerateArray())
            {
                var type = content.GetProperty("type").GetString();

                if (type == "tool_use")
                {
                    assistantContent.Add(content);

                    var toolName = content.GetProperty("name").GetString()!;
                    var toolId = content.GetProperty("id").GetString()!;
                    var inputs = content.TryGetProperty("input", out var inputProp) ? inputProp : (JsonElement?)null;

                    var result = DataTools.RunTool(toolName, inputs);
                    var resultJson = JsonSerializer.Serialize(result);

                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolId,
                        content = resultJson
                    });
                }
                else
                {
                    assistantContent.Add(content);
                }
            }

            messages.Add(new { role = "assistant", content = assistantContent });
            messages.Add(new { role = "user", content = toolResults });

            var nextRequestBody = new
            {
                model = "claude-sonnet-4-6",
                max_tokens = 1024,
                system = SystemPrompt,
                tools,
                messages
            };

            response = await SendAnthropicRequest(httpClient, nextRequestBody);
        }

        // Extract the final text reply
        var answer = "Sorry, I could not generate a response.";
        foreach (var content in response.GetProperty("content").EnumerateArray())
        {
            if (content.GetProperty("type").GetString() == "text")
            {
                answer = content.GetProperty("text").GetString() ?? answer;
                break;
            }
        }

        messages.Add(new { role = "assistant", content = answer });

        var history = messages.Select(m =>
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

async Task<JsonElement> SendAnthropicRequest(HttpClient client, object body)
{
    var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    var content = new StringContent(json);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    var response = await client.PostAsync("v1/messages", content);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"API returned {response.StatusCode}: {responseBody}", null, response.StatusCode);
    }

    return JsonDocument.Parse(responseBody).RootElement;
}

app.Run();
