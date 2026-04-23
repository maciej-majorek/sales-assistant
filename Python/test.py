from openai import AzureOpenAI
import os

client = AzureOpenAI(
  api_key = os.getenv("DIAL_API_KEY", ""),  # Put your DIAL API Key here
  api_version = "2024-02-01",
  azure_endpoint = "https://ai-proxy.lab.epam.com"
)

deployment_name = "gpt-4"

# Please only use this one if you absolutely need it. It's more expensive.
# deployment_name = "anthropic.claude-3-7-sonnet-20250219-v1:0"
# deployment_name = "gemini-2.5-flash"
# Use a GET request to the https://ai-proxy.lab.epam.com/openai/models endpoint to get the full list of currently available models.

# You can also retrieve embeddings, but small private models may perform better and be cheaper.
# https://huggingface.co/spaces/mteb/leaderboard
#
# Example usage for embeddings:
# embedding_model = "text-embedding-3-small-1"
# print(client.embeddings.create(
#     model=embedding_model,
#     input="Your text here"
# ))


print(client.chat.completions.create(
  model=deployment_name,
  temperature=0,
  messages=[
      {
        "role": "user",
        "content": "how are you?",
      },
  ],
))
