import pandas as pd

CSV_PATH = "sales.csv"


def get_csv_tools():
    return [
        {
            "name": "get_columns",
            "description": (
                "Return the column names and a sample of rows from sales.csv "
                "so you know what fields are available before querying."
            ),
            "input_schema": {"type": "object", "properties": {}},
        },
        {
            "name": "query_sales",
            "description": (
                "Execute a pandas expression against the sales DataFrame and return the result. "
                "Use this to answer questions about totals, averages, filters, top products, "
                "date ranges, region breakdowns, etc. "
                "Call get_columns first if you are unsure of the column names."
            ),
            "input_schema": {
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "description": (
                            "A Python/pandas expression evaluated with 'df' as the DataFrame "
                            "and 'pd' available. Examples:\n"
                            "  df['revenue'].sum()\n"
                            "  df[df['region']=='North']['revenue'].sum()\n"
                            "  df.groupby('product')['units'].sum().sort_values(ascending=False).head(5)"
                        ),
                    }
                },
                "required": ["query"],
            },
        },
    ]


def run_tool(name: str, inputs: dict):
    df = pd.read_csv(CSV_PATH, parse_dates=["date"])

    if name == "get_columns":
        return {
            "columns": df.columns.tolist(),
            "dtypes": df.dtypes.astype(str).to_dict(),
            "sample": df.head(3).to_dict(orient="records"),
            "row_count": len(df),
        }

    if name == "query_sales":
        try:
            result = eval(inputs["query"], {"df": df, "pd": pd})  # noqa: S307
            if hasattr(result, "to_dict"):
                return result.to_dict()
            if hasattr(result, "tolist"):
                return result.tolist()
            return result
        except Exception as e:
            return f"Query error: {e}"

    return f"Unknown tool: {name}"
