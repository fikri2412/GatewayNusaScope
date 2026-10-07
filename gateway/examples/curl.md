# Contoh integrasi — curl

Ganti `BASE` dan `GW_KEY` (API key gateway berformat `gw_<prefix>_<secret>`).

## Daftar model yang boleh dipakai key

```bash
curl -s "$BASE/v1/models" -H "Authorization: Bearer $GW_KEY"
```

## Chat completion (format OpenAI, respons utuh)

```bash
curl -s -X POST "$BASE/v1/chat/completions" \
  -H "Authorization: Bearer $GW_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"Halo"}],"max_tokens":512}'
```

## Chat completion streaming (SSE)

`-N` mematikan buffering curl supaya event langsung terlihat.

```bash
curl -N -s -X POST "$BASE/v1/chat/completions" \
  -H "Authorization: Bearer $GW_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"Halo"}],"stream":true,"stream_options":{"include_usage":true}}'
```

- `stream` harus boolean; nilai lain (mis. `1` atau `"true"`) ditolak `400` dengan code `invalid_stream`.
- Baca sampai `data: [DONE]`. Tanpa `stream_options.include_usage`, chunk usage dari upstream tidak diteruskan ke klien.
- Gagal di tengah stream: satu event terakhir `data: {"error":{...,"code":"upstream_stream_interrupted"|"upstream_stream_timeout"|"upstream_response_too_large"}}`
  lalu koneksi ditutup tanpa `[DONE]`.

## Login admin (tenant atau platform) dan pakai JWT

```bash
TOKEN=$(curl -s -X POST "$BASE/admin/api/auth/login" -H "Content-Type: application/json" \
  -d '{"email":"admin@example.com","password":"<password>"}' | jq -r .accessToken)

curl -s "$BASE/admin/api/projects" -H "Authorization: Bearer $TOKEN"
```

## Catatan

- Header `X-Request-Id` dikembalikan di setiap respons data plane; kirim `X-Gateway-Tags` untuk menandai request.
- Error berformat `{"error":{"message","type","param","code"}}`; `429` menyertakan `Retry-After`.
- Respons streaming memakai `Content-Type: text/event-stream; charset=utf-8`, `Cache-Control: no-cache`, dan `X-Accel-Buffering: no`.
