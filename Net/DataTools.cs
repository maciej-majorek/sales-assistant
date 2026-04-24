using System.Dynamic;
using System.Globalization;
using System.Linq.Dynamic.Core;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;

namespace SalesAssistant;

public static class DataTools
{
    private static readonly string CsvPath = Path.Combine("..", "sales.csv");

    public static List<object> GetCsvTools()
    {
        return
        [
            new
            {
                name = "get_columns",
                description = "Return the column names and a sample of rows from the CSV file so you know what fields are available before querying.",
                input_schema = new { type = "object", properties = new { } }
            },
            new
            {
                name = "query_sales",
                description = "Execute a Dynamic LINQ query against the data and return the result. " +
                              "Use this to answer questions about totals, averages, filters, top items, etc.\n" +
                              "Query patterns:\n" +
                              "  Sum(column) - total of a numeric column\n" +
                              "  Average(column) - average of a numeric column\n" +
                              "  Count() - count all rows\n" +
                              "  Min(column), Max(column) - min/max values\n" +
                              "  Where(column == \"value\") - filter rows\n" +
                              "  Where(column == \"value\").Sum(column) - filter then aggregate\n" +
                              "  OrderByDescending(column).Take(n) - top n by column\n" +
                              "  GroupBy(column) - group by column, returns each group with total_<numericColumn> sums\n" +
                              "  GroupBy(column).OrderByDescending(revenue).First() - highest group by revenue\n" +
                              "Call get_columns first if you are unsure of the column names.",
                input_schema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "A Dynamic LINQ expression to evaluate against the data. " +
                                          "Examples:\n" +
                                          "  Sum(revenue) - total revenue\n" +
                                          "  Where(region == \"North\").Sum(revenue) - revenue for North region\n" +
                                          "  GroupBy(region) - revenue by region (returns total_revenue per group)\n" +
                                          "  GroupBy(region).OrderByDescending(revenue).First() - region with highest revenue\n" +
                                          "  GroupBy(product).OrderByDescending(units) - products by units sold\n" +
                                          "  OrderByDescending(revenue).Take(5) - top 5 rows by revenue\n" +
                                          "  Where(customer == \"Acme Corp\").Count() - count orders for customer"
                        }
                    },
                    required = new[] { "query" }
                }
            }
        ];
    }

    public static object RunTool(string name, JsonElement? inputs)
    {
        var (result, _) = RunToolWithQuery(name, inputs);
        return result;
    }

    public static (object Result, string? Query) RunToolWithQuery(string name, JsonElement? inputs)
    {
        var (records, columns, dtypes) = LoadCsvDynamic();

        if (name == "get_columns")
        {
            var sample = records.Take(3).Select(r =>
            {
                var dict = (IDictionary<string, object?>)r;
                return dict.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.ToString() ?? ""
                );
            }).ToList();

            return (new
            {
                columns,
                dtypes,
                sample,
                row_count = records.Count
            }, null);
        }

        if (name == "query_sales")
        {
            var query = GetString(inputs, "query");
            if (string.IsNullOrEmpty(query))
            {
                return ("Error: query parameter is required", null);
            }

            try
            {
                var result = ExecuteDynamicQuery(records, query);
                return (result, query);
            }
            catch (Exception ex)
            {
                return ($"Query error: {ex.Message}", query);
            }
        }

        return ($"Unknown tool: {name}", null);
    }

    private static (List<ExpandoObject> Records, List<string> Columns, Dictionary<string, string> Dtypes) LoadCsvDynamic()
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
        };

        using var reader = new StreamReader(CsvPath);
        using var csv = new CsvReader(reader, config);

        var records = new List<ExpandoObject>();
        var columns = new List<string>();
        var dtypes = new Dictionary<string, string>();

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];

        foreach (var header in headers)
        {
            columns.Add(header);
            dtypes[header] = "string"; // Will be updated based on actual values
        }

        // Read all records and detect types
        var allRows = new List<string[]>();
        while (csv.Read())
        {
            var row = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                row[i] = csv.GetField(i) ?? "";
            }
            allRows.Add(row);
        }

        // Detect column types from first non-empty values
        for (int i = 0; i < headers.Length; i++)
        {
            var sampleValue = allRows.FirstOrDefault(r => !string.IsNullOrEmpty(r[i]))?[i] ?? "";
            dtypes[headers[i]] = DetectType(sampleValue);
        }

        // Convert rows to ExpandoObjects with proper types
        foreach (var row in allRows)
        {
            var record = new ExpandoObject() as IDictionary<string, object?>;

            for (int i = 0; i < headers.Length; i++)
            {
                var header = headers[i];
                var value = row[i];
                record[header] = ParseValue(value, dtypes[header]);
            }

            records.Add((ExpandoObject)record);
        }

        return (records, columns, dtypes);
    }

    private static string DetectType(string value)
    {
        if (string.IsNullOrEmpty(value)) return "string";
        if (int.TryParse(value, out _)) return "int";
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return "decimal";
        if (DateTime.TryParse(value, out _)) return "datetime";
        return "string";
    }

    private static object? ParseValue(string value, string type)
    {
        if (string.IsNullOrEmpty(value)) return null;

        return type switch
        {
            "int" => int.TryParse(value, out var i) ? i : value,
            "decimal" => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : value,
            "datetime" => DateTime.TryParse(value, out var dt) ? dt : value,
            _ => value
        };
    }

    private static object ExecuteDynamicQuery(List<ExpandoObject> records, string query)
    {
        if (records.Count == 0)
        {
            return "No data available";
        }

        var queryable = records.AsQueryable();
        var firstRecord = (IDictionary<string, object?>)records.First();

        // Helper to get actual column name (case-insensitive)
        string NormalizeColumn(string col) => FindColumn(firstRecord, col);

        // Helper to get column values manually (bypasses Dynamic LINQ case sensitivity)
        decimal SumColumn(IEnumerable<ExpandoObject> items, string col)
        {
            var actualCol = NormalizeColumn(col);
            return items
                .Select(r => GetColumnValue((IDictionary<string, object?>)r, actualCol))
                .Where(v => v != null)
                .Sum(v => Convert.ToDecimal(v!));
        }

        decimal AvgColumn(IEnumerable<ExpandoObject> items, string col)
        {
            var actualCol = NormalizeColumn(col);
            var values = items
                .Select(r => GetColumnValue((IDictionary<string, object?>)r, actualCol))
                .Where(v => v != null)
                .Select(v => Convert.ToDecimal(v!))
                .ToList();
            return values.Count > 0 ? values.Average() : 0;
        }

        // Handle aggregate functions that return a single value
        if (query.StartsWith("Sum(", StringComparison.OrdinalIgnoreCase))
        {
            var column = ExtractColumnName(query, "Sum");
            return SumColumn(records, column);
        }

        if (query.StartsWith("Average(", StringComparison.OrdinalIgnoreCase))
        {
            var column = ExtractColumnName(query, "Average");
            return AvgColumn(records, column);
        }

        if (query.StartsWith("Count(", StringComparison.OrdinalIgnoreCase))
        {
            return queryable.Count();
        }

        if (query.StartsWith("Min(", StringComparison.OrdinalIgnoreCase))
        {
            var column = NormalizeColumn(ExtractColumnName(query, "Min"));
            return records
                .Select(r => GetColumnValue((IDictionary<string, object?>)r, column))
                .Where(v => v != null)
                .Min()!;
        }

        if (query.StartsWith("Max(", StringComparison.OrdinalIgnoreCase))
        {
            var column = NormalizeColumn(ExtractColumnName(query, "Max"));
            return records
                .Select(r => GetColumnValue((IDictionary<string, object?>)r, column))
                .Where(v => v != null)
                .Max()!;
        }

        // Handle chained queries (e.g., Where(...).Sum(...))
        if (query.Contains('.'))
        {
            return ExecuteChainedQuery(queryable, query);
        }

        // Handle simple queries
        if (query.StartsWith("Where(", StringComparison.OrdinalIgnoreCase))
        {
            var predicate = query[6..^1]; // Remove "Where(" and ")"
            return ConvertDynamicListToDict(queryable.Where(predicate).ToDynamicList());
        }

        if (query.StartsWith("OrderBy(", StringComparison.OrdinalIgnoreCase))
        {
            var column = query[8..^1];
            return ConvertDynamicListToDict(queryable.OrderBy(column).ToDynamicList());
        }

        if (query.StartsWith("OrderByDescending(", StringComparison.OrdinalIgnoreCase))
        {
            var column = query[18..^1];
            return ConvertDynamicListToDict(queryable.OrderBy($"{column} descending").ToDynamicList());
        }

        if (query.StartsWith("GroupBy(", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteGroupByQuery(queryable, query);
        }

        if (query.StartsWith("Select(", StringComparison.OrdinalIgnoreCase))
        {
            var selector = query[7..^1];
            return queryable.Select(selector).ToDynamicList();
        }

        // Default: return all records
        return ConvertDynamicListToDict(queryable.ToDynamicList());
    }

    private static List<Dictionary<string, object?>> ConvertDynamicListToDict(List<dynamic> list)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var item in list)
        {
            if (item is ExpandoObject expando)
            {
                result.Add(ConvertExpandoToDict(expando));
            }
            else
            {
                result.Add(new Dictionary<string, object?> { ["value"] = item });
            }
        }
        return result;
    }

    private static object ExecuteChainedQuery(IQueryable<ExpandoObject> queryable, string query)
    {
        var parts = SplitQueryParts(query);
        var records = queryable.ToList();

        if (records.Count == 0)
        {
            return "No data available";
        }

        var firstRecord = (IDictionary<string, object?>)records.First();

        // Helper to normalize column names
        string NormalizeColumn(string col) => FindColumn(firstRecord, col);

        IQueryable current = records.AsQueryable();

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Count - 1;

            if (part.StartsWith("Where(", StringComparison.OrdinalIgnoreCase))
            {
                var predicate = part[6..^1];
                current = current.Where(predicate);
            }
            else if (part.StartsWith("OrderBy(", StringComparison.OrdinalIgnoreCase))
            {
                var column = NormalizeColumn(part[8..^1]);
                current = current.OrderBy(column);
            }
            else if (part.StartsWith("OrderByDescending(", StringComparison.OrdinalIgnoreCase))
            {
                var column = NormalizeColumn(part[18..^1]);
                current = current.OrderBy($"{column} descending");
            }
            else if (part.StartsWith("Take(", StringComparison.OrdinalIgnoreCase))
            {
                var count = int.Parse(part[5..^1]);
                current = current.Take(count);
            }
            else if (part.StartsWith("Skip(", StringComparison.OrdinalIgnoreCase))
            {
                var count = int.Parse(part[5..^1]);
                current = current.Skip(count);
            }
            else if (part.StartsWith("Sum(", StringComparison.OrdinalIgnoreCase) && isLast)
            {
                var column = NormalizeColumn(ExtractColumnName(part, "Sum"));
                var items = current.ToDynamicList().Cast<ExpandoObject>();
                return items
                    .Select(r => GetColumnValue((IDictionary<string, object?>)r, column))
                    .Where(v => v != null)
                    .Sum(v => Convert.ToDecimal(v!));
            }
            else if (part.StartsWith("Average(", StringComparison.OrdinalIgnoreCase) && isLast)
            {
                var column = NormalizeColumn(ExtractColumnName(part, "Average"));
                var items = current.ToDynamicList().Cast<ExpandoObject>();
                var values = items
                    .Select(r => GetColumnValue((IDictionary<string, object?>)r, column))
                    .Where(v => v != null)
                    .Select(v => Convert.ToDecimal(v!))
                    .ToList();
                return values.Count > 0 ? values.Average() : 0;
            }
            else if (part.StartsWith("Count(", StringComparison.OrdinalIgnoreCase) && isLast)
            {
                return current.Count();
            }
            else if (part.StartsWith("GroupBy(", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteGroupByQuery((IQueryable<ExpandoObject>)current, string.Join(".", parts[i..]));
            }
            else if (part.StartsWith("Select(", StringComparison.OrdinalIgnoreCase))
            {
                var selector = part[7..^1];
                current = current.Select(selector);
            }
        }

        return ConvertDynamicListToDict(current.ToDynamicList());
    }

    private static object ExecuteGroupByQuery(IQueryable<ExpandoObject> queryable, string query)
    {
        // Parse GroupBy(column) and optional chained operations
        var groupByMatch = System.Text.RegularExpressions.Regex.Match(
            query, @"GroupBy\((\w+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!groupByMatch.Success)
        {
            return "Invalid GroupBy syntax";
        }

        var groupColumnInput = groupByMatch.Groups[1].Value;

        // Get the records as a list for manual grouping
        var records = queryable.ToList();
        if (records.Count == 0)
        {
            return new List<Dictionary<string, object?>>();
        }

        // Find the actual column name (case-insensitive)
        var firstRecord = (IDictionary<string, object?>)records.First();
        var groupColumn = FindColumn(firstRecord, groupColumnInput);

        // Group manually using standard LINQ
        var groups = records
            .GroupBy(r => GetColumnValue((IDictionary<string, object?>)r, groupColumn))
            .Select(g =>
            {
                var items = g.ToList();
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [groupColumn] = g.Key,
                    ["count"] = items.Count
                };

                // Calculate aggregates for numeric columns
                var firstItem = items.FirstOrDefault();
                if (firstItem != null)
                {
                    var dict = (IDictionary<string, object?>)firstItem;
                    foreach (var kvp in dict)
                    {
                        if (kvp.Key.Equals(groupColumn, StringComparison.OrdinalIgnoreCase)) continue;

                        if (kvp.Value is int or decimal or double or float or long)
                        {
                            var values = items
                                .Select(i => GetColumnValue((IDictionary<string, object?>)i, kvp.Key))
                                .Where(v => v != null)
                                .Select(v => Convert.ToDecimal(v!));

                            result[$"total_{kvp.Key}"] = values.Sum();
                        }
                    }
                }

                return result;
            })
            .ToList();

        // Check for OrderBy/OrderByDescending after GroupBy
        var orderByDescMatch = System.Text.RegularExpressions.Regex.Match(
            query, @"\.OrderByDescending\((?:total_)?(\w+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var orderByMatch = System.Text.RegularExpressions.Regex.Match(
            query, @"\.OrderBy\((?:total_)?(\w+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (orderByDescMatch.Success && groups.Count > 0)
        {
            var orderCol = orderByDescMatch.Groups[1].Value;
            // Try total_<col> first (case-insensitive), then the column itself
            var colName = groups.First().Keys.FirstOrDefault(k => k.Equals($"total_{orderCol}", StringComparison.OrdinalIgnoreCase))
                       ?? groups.First().Keys.FirstOrDefault(k => k.Equals(orderCol, StringComparison.OrdinalIgnoreCase))
                       ?? orderCol;
            groups = groups.OrderByDescending(g => g.TryGetValue(colName, out var v) ? Convert.ToDecimal(v ?? 0) : 0).ToList();
        }
        else if (orderByMatch.Success && groups.Count > 0)
        {
            var orderCol = orderByMatch.Groups[1].Value;
            var colName = groups.First().Keys.FirstOrDefault(k => k.Equals($"total_{orderCol}", StringComparison.OrdinalIgnoreCase))
                       ?? groups.First().Keys.FirstOrDefault(k => k.Equals(orderCol, StringComparison.OrdinalIgnoreCase))
                       ?? orderCol;
            groups = groups.OrderBy(g => g.TryGetValue(colName, out var v) ? Convert.ToDecimal(v ?? 0) : 0).ToList();
        }

        // Check for Take
        var takeMatch = System.Text.RegularExpressions.Regex.Match(
            query, @"\.Take\((\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (takeMatch.Success)
        {
            var count = int.Parse(takeMatch.Groups[1].Value);
            groups = groups.Take(count).ToList();
        }

        // Check for First
        if (query.Contains(".First(", StringComparison.OrdinalIgnoreCase))
        {
            return groups.FirstOrDefault() ?? new Dictionary<string, object?>();
        }

        return groups;
    }

    private static List<string> SplitQueryParts(string query)
    {
        var parts = new List<string>();
        var current = "";
        var depth = 0;

        for (int i = 0; i < query.Length; i++)
        {
            var c = query[i];

            if (c == '(') depth++;
            else if (c == ')') depth--;

            if (c == '.' && depth == 0)
            {
                if (!string.IsNullOrEmpty(current))
                    parts.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }

        if (!string.IsNullOrEmpty(current))
            parts.Add(current);

        return parts;
    }

    private static string ExtractColumnName(string query, string functionName)
    {
        var start = functionName.Length + 1; // Skip "FunctionName("
        var end = query.IndexOf(')', start);
        return query[start..end];
    }

    private static Dictionary<string, object?> ConvertExpandoToDict(ExpandoObject expando)
    {
        var dict = (IDictionary<string, object?>)expando;
        return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static string? GetString(JsonElement? inputs, string key)
    {
        if (inputs == null) return null;
        if (inputs.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string FindColumn(IDictionary<string, object?> dict, string columnName)
    {
        // Try exact match first
        if (dict.ContainsKey(columnName)) return columnName;

        // Try case-insensitive match
        var match = dict.Keys.FirstOrDefault(k => k.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        return match ?? columnName;
    }

    private static object? GetColumnValue(IDictionary<string, object?> dict, string columnName)
    {
        var actualKey = FindColumn(dict, columnName);
        return dict.TryGetValue(actualKey, out var value) ? value : null;
    }
}
