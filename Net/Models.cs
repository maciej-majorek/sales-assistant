namespace SalesAssistant;

public record ChatRequest(string Message, List<MessageItem>? History = null);

public record ChatResponse(string Reply, List<MessageItem> History);

public record MessageItem(string Role, object Content);

public record SalesRecord
{
    public DateTime Date { get; set; }
    public string Product { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public int Units { get; set; }
    public decimal Revenue { get; set; }
    public string Customer { get; set; } = string.Empty;
}

public record ColumnsResult(
    List<string> Columns,
    Dictionary<string, string> Dtypes,
    List<Dictionary<string, object>> Sample,
    int RowCount
);
