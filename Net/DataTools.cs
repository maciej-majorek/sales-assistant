using System.Globalization;
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
                description = "Return the column names and a sample of rows from sales.csv so you know what fields are available before querying.",
                input_schema = new { type = "object", properties = new { } }
            },
            new
            {
                name = "query_sales",
                description = "Execute a query against the sales data and return the result. " +
                              "Use this to answer questions about totals, averages, filters, top products, " +
                              "date ranges, region breakdowns, etc. " +
                              "Call get_columns first if you are unsure of the column names.",
                input_schema = new
                {
                    type = "object",
                    properties = new
                    {
                        query_type = new
                        {
                            type = "string",
                            description = "Type of query: 'sum', 'average', 'count', 'filter', 'group_by', 'top', 'all'",
                            @enum = new[] { "sum", "average", "count", "filter", "group_by", "top", "all" }
                        },
                        column = new
                        {
                            type = "string",
                            description = "Column to aggregate (for sum, average, top queries): 'units' or 'revenue'"
                        },
                        group_by = new
                        {
                            type = "string",
                            description = "Column to group by (for group_by queries): 'product', 'region', 'customer'"
                        },
                        filter_column = new
                        {
                            type = "string",
                            description = "Column to filter on: 'product', 'region', 'customer', 'date'"
                        },
                        filter_value = new
                        {
                            type = "string",
                            description = "Value to filter for"
                        },
                        limit = new
                        {
                            type = "integer",
                            description = "Number of results to return (for top queries), default 5"
                        }
                    },
                    required = new[] { "query_type" }
                }
            }
        ];
    }

    public static object RunTool(string name, JsonElement? inputs)
    {
        var records = LoadCsv();

        if (name == "get_columns")
        {
            return new ColumnsResult(
                Columns: ["date", "product", "region", "units", "revenue", "customer"],
                Dtypes: new Dictionary<string, string>
                {
                    ["date"] = "datetime",
                    ["product"] = "string",
                    ["region"] = "string",
                    ["units"] = "int",
                    ["revenue"] = "decimal",
                    ["customer"] = "string"
                },
                Sample: records.Take(3).Select(r => new Dictionary<string, object>
                {
                    ["date"] = r.Date.ToString("yyyy-MM-dd"),
                    ["product"] = r.Product,
                    ["region"] = r.Region,
                    ["units"] = r.Units,
                    ["revenue"] = r.Revenue,
                    ["customer"] = r.Customer
                }).ToList(),
                RowCount: records.Count
            );
        }

        if (name == "query_sales")
        {
            return ExecuteQuery(records, inputs);
        }

        return $"Unknown tool: {name}";
    }

    private static List<SalesRecord> LoadCsv()
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            PrepareHeaderForMatch = args => args.Header.ToLower(),
        };

        using var reader = new StreamReader(CsvPath);
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<SalesRecord>().ToList();
    }

    private static string? GetString(JsonElement? inputs, string key)
    {
        if (inputs == null) return null;
        if (inputs.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int GetInt(JsonElement? inputs, string key, int defaultValue)
    {
        if (inputs == null) return defaultValue;
        if (inputs.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return defaultValue;
    }

    private static object ExecuteQuery(List<SalesRecord> records, JsonElement? inputs)
    {
        var queryType = GetString(inputs, "query_type") ?? "all";
        var column = GetString(inputs, "column");
        var groupBy = GetString(inputs, "group_by");
        var filterColumn = GetString(inputs, "filter_column");
        var filterValue = GetString(inputs, "filter_value");
        var limit = GetInt(inputs, "limit", 5);

        // Apply filter if specified
        if (!string.IsNullOrEmpty(filterColumn) && !string.IsNullOrEmpty(filterValue))
        {
            records = filterColumn.ToLower() switch
            {
                "product" => records.Where(r => r.Product.Equals(filterValue, StringComparison.OrdinalIgnoreCase)).ToList(),
                "region" => records.Where(r => r.Region.Equals(filterValue, StringComparison.OrdinalIgnoreCase)).ToList(),
                "customer" => records.Where(r => r.Customer.Equals(filterValue, StringComparison.OrdinalIgnoreCase)).ToList(),
                "date" => records.Where(r => r.Date.ToString("yyyy-MM-dd").Contains(filterValue)).ToList(),
                _ => records
            };
        }

        return queryType.ToLower() switch
        {
            "sum" => column?.ToLower() switch
            {
                "units" => new { total_units = records.Sum(r => r.Units) },
                "revenue" => new { total_revenue = records.Sum(r => r.Revenue) },
                _ => new { total_units = records.Sum(r => r.Units), total_revenue = records.Sum(r => r.Revenue) }
            },
            "average" => column?.ToLower() switch
            {
                "units" => new { average_units = records.Average(r => r.Units) },
                "revenue" => new { average_revenue = records.Average(r => r.Revenue) },
                _ => new { average_units = records.Average(r => r.Units), average_revenue = records.Average(r => r.Revenue) }
            },
            "count" => new { count = records.Count },
            "group_by" => groupBy?.ToLower() switch
            {
                "product" => records.GroupBy(r => r.Product)
                    .Select(g => new { product = g.Key, total_units = g.Sum(r => r.Units), total_revenue = g.Sum(r => r.Revenue) })
                    .OrderByDescending(x => x.total_revenue).ToList(),
                "region" => records.GroupBy(r => r.Region)
                    .Select(g => new { region = g.Key, total_units = g.Sum(r => r.Units), total_revenue = g.Sum(r => r.Revenue) })
                    .OrderByDescending(x => x.total_revenue).ToList(),
                "customer" => records.GroupBy(r => r.Customer)
                    .Select(g => new { customer = g.Key, total_units = g.Sum(r => r.Units), total_revenue = g.Sum(r => r.Revenue) })
                    .OrderByDescending(x => x.total_revenue).ToList(),
                _ => "Please specify group_by column: 'product', 'region', or 'customer'"
            },
            "top" => column?.ToLower() switch
            {
                "units" => records.OrderByDescending(r => r.Units).Take(limit)
                    .Select(r => new { r.Date, r.Product, r.Region, r.Units, r.Revenue, r.Customer }).ToList(),
                "revenue" or _ => records.OrderByDescending(r => r.Revenue).Take(limit)
                    .Select(r => new { r.Date, r.Product, r.Region, r.Units, r.Revenue, r.Customer }).ToList()
            },
            "all" => records.Select(r => new { r.Date, r.Product, r.Region, r.Units, r.Revenue, r.Customer }).ToList(),
            _ => "Unknown query type. Use: sum, average, count, filter, group_by, top, or all"
        };
    }
}
