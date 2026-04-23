from fastapi import FastAPI, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from openai import AzureOpenAI, AuthenticationError, RateLimitError, APIError
import json
import os

from data_tools import get_csv_tools, run_tool

app = FastAPI(title="Sales Assistant")

# DIAL platform configuration
DIAL_URL = "https://ai-proxy.lab.epam.com"
DIAL_DEPLOYMENT = "gpt-4"
DIAL_API_VERSION = "2024-02-01"
DIAL_API_KEY = os.getenv("DIAL_API_KEY", "")

client = AzureOpenAI(
    azure_endpoint=DIAL_URL,
    api_key=DIAL_API_KEY,
    api_version=DIAL_API_VERSION,
)

# Enable CORS for cross-origin requests from different backend ports
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.mount("/static", StaticFiles(directory="../static"), name="static")

SYSTEM_PROMPT = (
    "You are a helpful sales assistant. "
    "Use the provided tools to query sales data from sales.csv and answer user questions accurately. "
    "Always base your answers on real data retrieved via tools. "
    "When presenting numbers, format them clearly (e.g. use $ for currency, commas for thousands). "
    "If a question is not related to sales data, politely let the user know."
)


def convert_tools_to_openai_format(anthropic_tools: list) -> list:
    """Convert Anthropic tool format to OpenAI function format."""
    openai_tools = []
    for tool in anthropic_tools:
        openai_tools.append({
            "type": "function",
            "function": {
                "name": tool["name"],
                "description": tool["description"],
                "parameters": tool["input_schema"],
            }
        })
    return openai_tools


class ChatRequest(BaseModel):
    message: str
    history: list = []


@app.get("/")
def root():
    return FileResponse("../static/index.html")


@app.post("/chat")
def chat(req: ChatRequest):
    tools = get_csv_tools()
    openai_tools = convert_tools_to_openai_format(tools)

    # Build messages in OpenAI format with system message first
    messages = [{"role": "system", "content": SYSTEM_PROMPT}]
    messages.extend(req.history)
    messages.append({"role": "user", "content": req.message})

    try:
        response = client.chat.completions.create(
            model=DIAL_DEPLOYMENT,
            messages=messages,
            tools=openai_tools,
            max_tokens=1024,
        )

        assistant_message = response.choices[0].message

        # Agentic tool-use loop: keep calling until model stops requesting tools
        while assistant_message.tool_calls:
            # Add assistant message with tool calls to history
            messages.append(assistant_message.model_dump())

            # Process each tool call and add results
            for tool_call in assistant_message.tool_calls:
                tool_name = tool_call.function.name
                tool_args = json.loads(tool_call.function.arguments) if tool_call.function.arguments else {}

                result = run_tool(tool_name, tool_args)

                # Add tool result message
                messages.append({
                    "role": "tool",
                    "tool_call_id": tool_call.id,
                    "content": str(result),
                })

            # Call API again with tool results
            response = client.chat.completions.create(
                model=DIAL_DEPLOYMENT,
                messages=messages,
                tools=openai_tools,
                max_tokens=1024,
            )
            assistant_message = response.choices[0].message

    except AuthenticationError as e:
        raise HTTPException(status_code=401, detail=f"Authentication failed: {e.message}")
    except RateLimitError as e:
        raise HTTPException(status_code=429, detail=f"Rate limit exceeded: {e.message}")
    except APIError as e:
        raise HTTPException(status_code=e.status_code or 500, detail=f"API error: {e.message}")

    # Extract the final text reply
    answer = assistant_message.content or "Sorry, I could not generate a response."
    messages.append({"role": "assistant", "content": answer})

    # Return history without system message for client storage
    return {"reply": answer, "history": messages[1:]}
