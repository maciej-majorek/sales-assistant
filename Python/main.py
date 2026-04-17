from fastapi import FastAPI, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import anthropic

from data_tools import get_csv_tools, run_tool

app = FastAPI(title="Sales Assistant")
client = anthropic.Anthropic()  # reads ANTHROPIC_API_KEY from environment

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


class ChatRequest(BaseModel):
    message: str
    history: list = []


@app.get("/")
def root():
    return FileResponse("../static/index.html")


@app.post("/chat")
def chat(req: ChatRequest):
    tools = get_csv_tools()
    messages = req.history + [{"role": "user", "content": req.message}]

    try:
        response = client.messages.create(
            model="claude-sonnet-4-6",
            max_tokens=1024,
            system=SYSTEM_PROMPT,
            tools=tools,
            messages=messages,
        )

        # Agentic tool-use loop: keep calling until Claude stops requesting tools
        while response.stop_reason == "tool_use":
            tool_results = []
            for block in response.content:
                if block.type == "tool_use":
                    result = run_tool(block.name, block.input)
                    tool_results.append(
                        {
                            "type": "tool_result",
                            "tool_use_id": block.id,
                            "content": str(result),
                        }
                    )

            messages += [
                {"role": "assistant", "content": response.content},
                {"role": "user", "content": tool_results},
            ]

            response = client.messages.create(
                model="claude-sonnet-4-6",
                max_tokens=1024,
                system=SYSTEM_PROMPT,
                tools=tools,
                messages=messages,
            )

    except anthropic.AuthenticationError as e:
        raise HTTPException(status_code=401, detail=f"Authentication failed: {e.message}")
    except anthropic.RateLimitError as e:
        raise HTTPException(status_code=429, detail=f"Rate limit exceeded: {e.message}")
    except anthropic.APIError as e:
        raise HTTPException(status_code=e.status_code or 500, detail=f"API error: {e.message}")

    # Extract the final text reply
    answer = next(
        (block.text for block in response.content if hasattr(block, "text")),
        "Sorry, I could not generate a response.",
    )
    messages.append({"role": "assistant", "content": answer})

    return {"reply": answer, "history": messages}
