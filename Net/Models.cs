namespace SalesAssistant;

public record ChatRequest(string Message, List<MessageItem>? History = null);

public record ChatResponse(string Reply, List<MessageItem> History, List<string>? Queries = null);

public record MessageItem(string Role, object Content);
