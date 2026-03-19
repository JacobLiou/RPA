import json
import os

payload = os.environ.get("RPA_INPUT_JSON", "{}")
data = json.loads(payload)

a = int(data.get("A", 0))
b = int(data.get("B", 0))

print(json.dumps({"sum": a + b}))
