#!/usr/bin/env python3
"""Contoh klien AI Gateway (Python 3, hanya stdlib).

Pakai:
    python example.py http://localhost:5090 gw_<prefix>_<secret>

Memanggil GET /v1/models dan POST /v1/chat/completions dengan format OpenAI.
"""

import json
import sys
import urllib.error
import urllib.request


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


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    base, key = sys.argv[1], sys.argv[2]

    models = call(base, "/v1/models", key)
    ids = [model["id"] for model in models["data"]]
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


if __name__ == "__main__":
    main()
