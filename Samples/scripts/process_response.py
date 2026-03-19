import json
import os

payload = os.environ.get("RPA_INPUT_JSON", "{}")
data = json.loads(payload)

status_code = data.get("HttpStatusCode", 0)
body_raw = data.get("HttpBody", "")

try:
    body_json = json.loads(body_raw)
except Exception:
    body_json = {}

result = {
    "statusOk": status_code == 200,
    "title": body_json.get("title", "N/A"),
    "summary": f"HTTP {status_code} - keys: {list(body_json.keys())}"
}

print(json.dumps(result))
