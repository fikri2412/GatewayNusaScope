#!/usr/bin/env python3
"""Contoh klien AI Gateway (Python 3.10+, hanya stdlib).

Pakai:
    python example.py <base_url> <api_key> [--reasoning]

Memanggil GET /v1/models, POST /v1/chat/completions (respons utuh), lalu endpoint yang sama dengan
"stream": true (SSE). --reasoning menampilkan delta reasoning_content; default disembunyikan.
"""

import json
import sys
import urllib.error
import urllib.request

# Gateway membatasi durasi total stream (Proxy:MaxStreamSeconds) dan jeda antar baris
# (Proxy:UpstreamTimeoutSeconds); timeout klien dibuat lebih longgar dari keduanya.
STREAM_TIMEOUT_SECONDS = 960


def call(base: str, path: str, key: str, payload: dict | None = None) -> dict:
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(
        base.rstrip("/") + path,
        data=data,
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
        method="POST" if data else "GET",
    )
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        # Error gateway selalu {"error": {"message", "type", "param", "code"}}.
        raise SystemExit(f"HTTP {error.code}: {error.read().decode()}")


def stream(base: str, path: str, key: str, payload: dict, show_reasoning: bool) -> None:
    """POST dengan "stream": true; cetak delta.content dan ringkas usage di akhir."""
    request = urllib.request.Request(
        base.rstrip("/") + path,
        data=json.dumps(payload).encode(),
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
        method="POST",
    )
    usage = None
    finish = None
    in_reasoning = False
    try:
        with urllib.request.urlopen(request, timeout=STREAM_TIMEOUT_SECONDS) as response:
            for raw in response:  # baris demi baris, tanpa menunggu seluruh body
                line = raw.decode("utf-8", "replace").rstrip("\r\n")
                if not line.startswith("data:"):
                    continue  # komentar/event:/id:/retry: yang diteruskan gateway
                data = line[5:].strip()
                if data == "[DONE]":
                    break
                try:
                    chunk = json.loads(data)
                except json.JSONDecodeError:
                    continue
                if isinstance(chunk.get("error"), dict):
                    error = chunk["error"]
                    raise SystemExit(f"stream error: {error.get('code')} — {error.get('message')}")
                # Chunk usage-saja (choices kosong) hanya datang karena kita memintanya lewat
                # stream_options.include_usage.
                if isinstance(chunk.get("usage"), dict):
                    usage = chunk["usage"]
                for choice in chunk.get("choices") or []:
                    if choice.get("finish_reason"):
                        finish = choice["finish_reason"]
                    delta = choice.get("delta") or {}
                    if show_reasoning and delta.get("reasoning_content"):
                        if not in_reasoning:
                            print("\n[reasoning] ", end="", flush=True)
                            in_reasoning = True
                        print(delta["reasoning_content"], end="", flush=True)
                    if delta.get("content"):
                        if in_reasoning:
                            print("\n[jawaban] ", end="", flush=True)
                            in_reasoning = False
                        print(delta["content"], end="", flush=True)
    except urllib.error.HTTPError as error:
        raise SystemExit(f"HTTP {error.code}: {error.read().decode()}")
    print()
    print(f"stream selesai: finish={finish} usage={usage}")


def main() -> None:
    argv = [arg for arg in sys.argv[1:] if arg != "--reasoning"]
    show_reasoning = "--reasoning" in sys.argv
    if len(argv) != 2:
        raise SystemExit(__doc__)
    base, key = argv

    models = call(base, "/v1/models", key)
    ids = [model["id"] for model in models["data"]]
    if not ids:
        raise SystemExit("Tidak ada model yang boleh dipakai key ini.")
    print(f"{len(ids)} model: {', '.join(ids[:5])}{' ...' if len(ids) > 5 else ''}")

    model = ids[0]
    answer = call(base, "/v1/chat/completions", key, {
        "model": model,
        "messages": [{"role": "user", "content": "Balas satu kata: halo"}],
        "max_tokens": 512,
    })
    choice = answer["choices"][0]
    print(f"model={answer['model']} finish={choice['finish_reason']}")
    print(f"jawaban: {choice['message'].get('content')!r}")
    print(f"usage: {answer['usage']}")

    print("\nstreaming (stream=true):")
    stream(base, "/v1/chat/completions", key, {
        "model": model,
        "messages": [{"role": "user", "content": "Hitung 1 sampai 5."}],
        "max_tokens": 512,
        "stream": True,
        "stream_options": {"include_usage": True},
    }, show_reasoning)


if __name__ == "__main__":
    main()
