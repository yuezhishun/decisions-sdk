"""Exercise the standalone Decisions Demo without calling either real provider."""

import argparse
import base64
import gzip
import http.client
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import struct
import subprocess
import tempfile
import threading
import time
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


KEY = "local-contract-test-key"
MODELS = {"codex": "fixture-model", "deepseek": "deepseek-flash"}
LIMIT = 32 * 1024 * 1024
ZSTD_JEV = base64.b64decode(
    "KLUv/SB+LQMAIocWGZCpORjKFkEkkiEQhFrSMGOnml4NWfZ/jAGtNQfweaRxpeR7SI6owMZSrKJ08VkJ2fiK7ZkXxopSrn4lXMwO9YtACij3H9j4e6suNuidA3D4Vf1o43NMCC8CAgCRSibUFjM="
)


def encoded(value):
    return json.dumps(value, separators=(",", ":")).encode()


def request(port, route, payload=None, headers=None, *, raw=None, token=KEY):
    options = {"Content-Type": "application/json"}
    if token is not None:
        options["Authorization"] = f"Bearer {token}"
    options.update(headers or {})
    body = raw if raw is not None else encoded(payload) if payload is not None else None
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=45)
    try:
        connection.request("POST" if body is not None else "GET", route, body, options)
        with connection.getresponse() as response:
            data = response.read()
            return response.status, json.loads(data) if data and "json" in response.getheader("Content-Type", "") else data
    finally:
        connection.close()


class DeepSeekFixture(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self):
        assert self.path == "/chat/completions", self.path
        assert self.headers["Authorization"] == "Bearer controlled-provider-key"
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.server.calls.append(body)
        content = json.dumps(body["messages"], ensure_ascii=False)
        if "fixture:cancel" in content:
            self.server.cancel_started.set()
            self.connection.settimeout(10)
            if not self.connection.recv(1):
                self.server.cancelled.set()
            return

        finish = "stop"
        if "fixture:refuse" in content:
            finish, result = "refusal", None
        elif "Question (box)" in content or '"box"' in content or '\\"box\\"' in content:
            assert "data:image/" in content
            result = {"box": {"left": 0.2, "top": 0.1, "right": 0.8, "bottom": 0.9}}
        elif "Question (choice)" in content and "named-a" in content:
            result = {"probabilities": {"named-a": 0.8, "named-b": 0.2}}
        elif "Question (choice)" in content or "Question (score)" in content:
            result = {"probabilities": {"0": 0.8, "1": 0.2}}
        else:
            result = {"noul": 0.8}
        reply = encoded({"model": MODELS["deepseek"], "choices": [
            {"index": 0, "finish_reason": finish, "message": {
                "role": "assistant", "content": None if result is None else encoded(result).decode()
            }}],
            "usage": {"prompt_tokens": 20, "completion_tokens": 5, "total_tokens": 25,
                      "prompt_cache_hit_tokens": 2, "completion_tokens_details": {"reasoning_tokens": 0}}})
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(reply)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(reply)

    def log_message(self, *_):
        pass


def png(width=280, height=280):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    return (
        b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress((b"\0" + b"\xff\0\0" * width) * height)) + chunk(b"IEND", b"")
    )


def multipart(payload, image):
    return b"".join([
        b'--demo-border\r\nContent-Disposition: form-data; name="request"\r\nContent-Type: application/json\r\n\r\n',
        encoded(payload),
        b'\r\n--demo-border\r\nContent-Disposition: form-data; name="photo"; filename="photo.png"\r\n',
        b'Content-Type: image/png\r\n\r\n', image, b"\r\n--demo-border--\r\n"
    ])


def jev_request(model):
    return {"model": model, "state": "Meeting Tuesday", "questions": {
        "yes": {"type": "noul", "instructions": "Is a date specified?"}}}


def verify_backend(port, backend, model):
    base = f"/{backend}/v1"
    assert request(port, base + "/models", token=None)[0] == 401
    assert request(port, base + "/models", token="wrong")[0] == 401
    status, models = request(port, base + "/models")
    assert status == 200 and models["models"][0]["name"] == model, (status, models)
    status, error = request(port, base + "/systemone", {"model": model})
    assert status == 422 and error["detail"][0]["loc"][0] == "body", (backend, status, error)
    assert request(port, base + "/systemone", jev_request(model),
                   {"TypeSafe-Danger-Zone-Images": "false"})[0] == 422
    openai = {
        "model": model,
        "input": [
            {"type": "message", "role": "system", "content": "A transcript to evaluate."},
            {"type": "message", "role": "developer", "content": "Evaluate the meeting."},
            {"type": "message", "role": "user", "content": [
                {"type": "input_json", "data": {"day": "Tuesday"}}]},
            {"type": "function_call", "name": "lookup", "call_id": "call_historical", "arguments": "{}"},
            {"type": "function_call_output", "call_id": "call_historical", "output": "Tuesday"},
            {"type": "custom_tool_call", "name": "label", "namespace": "fixture_tools",
             "call_id": "call_label", "input": "meeting"},
            {"type": "custom_tool_call_output", "call_id": "call_label", "output": "scheduled"},
            {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "Tuesday"}]},
        ],
        "questions": [
            {"type": "predicate", "name": "same", "instructions": "Is a day specified?"},
            {"type": "choice", "name": "same", "instructions": "Pick an option.",
             "choices": [{"value": True}, {"value": "true"}]},
            {"type": "score", "instructions": "How specific?", "levels": [{"label": "low"}, {"label": "high"}]},
            {"type": "predicate", "name": "denied", "instructions": "fixture:refuse"},
        ],
    }
    status, result = request(port, base + "/decisions", openai)
    assert status == 200, (backend, status, result)
    assert [answer["type"] for answer in result["answers"]] == ["predicate", "choice", "score", "refusal"], result
    assert [answer["name"] for answer in result["answers"]] == ["same", "same", None, "denied"], result
    assert [entry["value"] for entry in result["answers"][1]["probabilities"]] == [True, "true"], result
    assert result["model"] == model and result["usage"]["total_tokens"] > 0, result
    status, error = request(port, base + "/decisions", dict(openai, prompt_cache_key="unsupported"))
    assert status == 400 and error["error"]["code"] == "unsupported_capability", (status, error)
    invalid = dict(openai, model="unknown")
    assert request(port, base + "/decisions", invalid)[0] == 400
    assert request(port, base + "/decisions",
                   dict(openai, input=[openai["input"][4]]))[0] == 400

    vision = dict(openai, input=[{"type": "message", "role": "user", "content": [
        {"type": "input_image", "image_url": "data:image/png;base64," + base64.b64encode(png()).decode(),
         "detail": "auto"}]}], questions=openai["questions"][:1])
    status, image_answer = request(port, base + "/decisions", vision)
    assert status == 200 and image_answer["answers"][0]["type"] == "predicate", (backend, status, image_answer)
    if backend == "codex":
        vision["input"][0]["content"][0]["image_url"] = "http://127.0.0.1/image.png"
        assert request(port, base + "/decisions", vision)[0] == 400

    inline = jev_request(model)
    inline["state"] = {"photo": "reference", "date": ["Tuesday"]}
    inline["assets"] = {"reference": {"type": "image", "format": "png", "data_base64": base64.b64encode(png()).decode()}}
    inline["questions"].update({
        "pick": {"type": "choice", "instructions": ["Pick one."], "criteria": {"named-a": "Yes", "named-b": "No"}},
        "grade": {"type": "score", "instructions": {"text": "How specific?"}, "criteria": ["low", "high"]},
        "frame": {"type": "box", "instructions": "Locate the red object in photo."},
    })
    images = {"TypeSafe-Danger-Zone-Images": "true"}
    assert request(port, base + "/systemone", inline)[0] in (400, 422)
    status, direct = request(port, base + "/systemone", inline, images)
    assert status == 200, (backend, status, direct)
    assert set(direct["answers"]) == {"yes", "pick", "grade", "frame"}
    assert direct["answers"]["frame"]["confidence"] is None
    assert direct["answers"]["frame"]["box"]["left"] == 0.2
    assert direct["answers"]["pick"]["choice"] == "named-a"
    assert direct["answers"]["grade"]["legend"] == {"0": "low", "1": "high"}
    form = dict(inline, assets={"reference": {"type": "image", "part": "photo"}})
    media = dict(images, **{"Content-Type": "multipart/form-data; boundary=demo-border"})
    status, uploaded = request(port, base + "/systemone", headers=media, raw=multipart(form, png()))
    assert status == 200 and uploaded["answers"] == direct["answers"], (backend, status, uploaded)
    for invalid_image in (b"not PNG", png(16, 16)):
        status, error = request(port, base + "/systemone", headers=media, raw=multipart(form, invalid_image))
        assert status in (400, 422) and "detail" in error, (status, error)
    missing = dict(form, assets={"reference": {"type": "image", "part": "different"}})
    assert request(port, base + "/systemone", headers=media, raw=multipart(missing, png()))[0] in (400, 422)

    for encoding in ("gzip", "x-gzip"):
        assert request(port, base + "/systemone", headers={"Content-Encoding": encoding},
                       raw=gzip.compress(encoded(jev_request(model))))[0] == 200
    status, error = request(port, base + "/systemone", payload=jev_request(model), headers={"Content-Encoding": "br"})
    assert status == 415 and "detail" in error, (status, error)
    assert request(port, base + "/systemone", raw=b"broken gzip", headers={"Content-Encoding": "gzip"})[0] == 400


def verify_cancel(port, fixture):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    payload = jev_request(MODELS["deepseek"])
    payload["state"] = "fixture:cancel"
    try:
        connection.request("POST", "/deepseek/v1/systemone", encoded(payload),
                           {"Content-Type": "application/json", "Authorization": f"Bearer {KEY}"})
        assert fixture.cancel_started.wait(10), "Cancellation target never reached the controlled model."
        connection.sock.shutdown(socket.SHUT_RDWR)
    finally:
        connection.close()
    assert fixture.cancelled.wait(10), "The downstream connection did not close after client cancellation."


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("demo", type=Path)
    parser.add_argument("codex_fixture", type=Path)
    parser.add_argument("sdk_probe", type=Path)
    args = parser.parse_args()
    demo, codex_fixture, sdk_probe = [path.resolve(strict=True) for path in (args.demo, args.codex_fixture, args.sdk_probe)]
    with tempfile.TemporaryDirectory(prefix="decisions-demo-") as directory, \
            ThreadingHTTPServer(("127.0.0.1", 0), DeepSeekFixture) as provider:
        stage = Path(directory)
        fixture_folder = stage / "fixture"
        shutil.copytree(codex_fixture.parent, fixture_folder)
        executable = fixture_folder / codex_fixture.name
        (fixture_folder / "mode.txt").write_text("normal", encoding="utf-8")
        provider.calls = []
        provider.cancel_started, provider.cancelled = threading.Event(), threading.Event()
        service = threading.Thread(target=provider.serve_forever, daemon=True)
        service.start()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        env = dict(os.environ, DECISIONS_DEMO_API_KEY=KEY, DEEPSEEK_API_KEY="controlled-provider-key",
                   SAG_FIXTURE_FORBIDDEN_ENVIRONMENT="DECISIONS_DEMO_API_KEY",
                   DecisionsDemo__Port=str(port), DecisionsDemo__CodexExecutable=str(executable),
                   DecisionsDemo__CodexModel=MODELS["codex"], DecisionsDemo__DeepSeekModel=MODELS["deepseek"],
                   DecisionsDemo__DeepSeekEndpoint=f"http://127.0.0.1:{provider.server_port}/",
                   DecisionsDemo__CodexReleaseDate="2026-01-01", DecisionsDemo__DeepSeekReleaseDate="2026-01-01",
                   NO_PROXY="127.0.0.1,localhost")
        logfile = stage / "demo.log"
        process = None
        try:
            with logfile.open("wb") as log:
                process = subprocess.Popen(["dotnet", str(demo)], cwd=stage, env=env, stdout=log, stderr=subprocess.STDOUT,
                                           creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
                deadline = time.monotonic() + 20
                while True:
                    try:
                        if request(port, "/deepseek/v1/models")[0] == 200:
                            break
                    except OSError:
                        pass
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise AssertionError("Demo did not start.")
                    time.sleep(0.1)
                verify_backend(port, "codex", MODELS["codex"])
                verify_backend(port, "deepseek", MODELS["deepseek"])
                calls = [json.loads(line) for line in (fixture_folder / "events.jsonl").read_text(encoding="utf-8").splitlines()]
                assert any(event["phase"] == "protocol" and event["images"] > 0 for event in calls)
                assert any(event["phase"] == "history" and
                           [item["type"] for item in event["items"]].count("custom_tool_call_output") == 1
                           for event in calls)
                assert any(event["phase"] == "history" and
                           any(item.get("namespace") == "fixture_tools" for item in event["items"]) for event in calls)
                histories = [event["items"] for event in calls if event["phase"] == "history"]
                assert any([item["content"][0]["text"] for item in history[:2]] == [
                    "Original history role (evaluation data): system",
                    "Original history role (evaluation data): developer",
                ] and all(item["role"] == "user" for item in history[:2]) for history in histories)
                assert any("call_label" in json.dumps(call["messages"]) for call in provider.calls)
                assert request(port, "/deepseek/v1/systemone", headers={"Content-Encoding": "zstd"}, raw=ZSTD_JEV)[0] == 200
                assert request(port, "/deepseek/v1/systemone", headers={"Content-Encoding": "gzip"},
                               raw=gzip.compress(b" " * (LIMIT + 1)))[0] == 413
                assert request(port, "/v1/decisions", {})[0] == 404
                verify_cancel(port, provider)
                sdk = subprocess.run(["dotnet", str(sdk_probe), f"http://127.0.0.1:{port}/",
                                      MODELS["codex"], MODELS["deepseek"]], env=env, timeout=90,
                                     capture_output=True, text=True, encoding="utf-8")
                assert sdk.returncode == 0, sdk.stdout + sdk.stderr
                print(sdk.stdout, end="")
                print("OpenAI and Jev passed on both backends, including multipart, gzip, x-gzip, zstd, errors and cancellation.")
        except Exception:
            print("\n".join(logfile.read_text(encoding="utf-8", errors="replace").splitlines()[-35:]))
            raise
        finally:
            if process is not None and process.poll() is None:
                try:
                    process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
                    process.wait(timeout=10)
                except (OSError, subprocess.TimeoutExpired):
                    process.kill()
                    process.wait(timeout=10)
            provider.shutdown()


if __name__ == "__main__":
    main()
