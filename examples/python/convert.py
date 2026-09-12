"""Minimal async conversion example. Requires Python 3.11+; uses only the standard library."""
import json
import os
import sys
import time
import uuid
from urllib import request

base_url = os.getenv("INACTIVEPDF_URL", "http://127.0.0.1:5080")
token = os.getenv("INACTIVEPDF_TOKEN")
if not token:
    raise SystemExit("Set INACTIVEPDF_TOKEN before running this example.")
path = sys.argv[1] if len(sys.argv) > 1 else "document.docx"
boundary = "----InactivePDF" + uuid.uuid4().hex
fields = [("mode", "async"), ("profile", "archive")]
body = bytearray()
for name, value in fields:
    body.extend(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode())
with open(path, "rb") as source:
    body.extend(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{os.path.basename(path)}\"\r\nContent-Type: application/octet-stream\r\n\r\n".encode())
    body.extend(source.read())
body.extend(f"\r\n--{boundary}--\r\n".encode())

def call(url, method="GET", data=None, content_type=None):
    headers = {"Authorization": f"Bearer {token}"}
    if content_type:
        headers["Content-Type"] = content_type
    if method == "POST":
        headers["Idempotency-Key"] = f"python-{uuid.uuid4()}"
    with request.urlopen(request.Request(url, data=data, method=method, headers=headers)) as response:
        return response.status, response.read()

_, response = call(f"{base_url}/v1/conversions", "POST", bytes(body), f"multipart/form-data; boundary={boundary}")
job = json.loads(response)
while True:
    _, response = call(f"{base_url}/v1/jobs/{job['jobId']}")
    status = json.loads(response)
    if status["state"] in {"Succeeded", "Failed", "Cancelled", "DeadLettered", "Interrupted"}:
        break
    time.sleep(0.5)
if status["state"] != "Succeeded":
    raise SystemExit(f"Conversion ended in {status['state']}: {status.get('errorCode', 'unknown error')}")
_, pdf = call(f"{base_url}/v1/jobs/{job['jobId']}/output")
with open("converted.pdf", "wb") as output:
    output.write(pdf)
print("Converted to converted.pdf")
