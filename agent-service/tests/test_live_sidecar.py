import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import httpx


def _free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def test_live_uvicorn_sidecar_health_and_shutdown(tmp_path: Path):
    port = _free_port()
    environment = os.environ.copy()
    environment.pop("DEEPSEEK_API_KEY", None)
    environment["APP_ASSISTANT_DATA_DIR"] = str(tmp_path / "assistant-data")
    environment["APP_ASSISTANT_APIHOST_URL"] = "http://127.0.0.1:1"

    process = subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "app_assistant.server:app", "--host", "127.0.0.1", "--port", str(port)],
        cwd=Path(__file__).parents[1],
        env=environment,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        deadline = time.monotonic() + 15
        health = None
        with httpx.Client(timeout=0.5) as client:
            while time.monotonic() < deadline:
                try:
                    health = client.get(f"http://127.0.0.1:{port}/health")
                    if health.status_code == 200:
                        break
                except httpx.HTTPError:
                    pass
                time.sleep(0.1)

        assert health is not None
        assert health.status_code == 200
        assert health.json()["status"] == "ok"
        assert health.json()["graphVersion"] == "0.1.0"
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)

    assert process.poll() is not None
