# Contoh integrasi — curl

Ganti `BASE` dan `GW_KEY` (API key gateway berformat `gw_<prefix>_<secret>`).

## Daftar model yang boleh dipakai key

```bash
curl -s "$BASE/v1/models" -H "Authorization: Bearer $GW_KEY"
```

## Chat completion (format OpenAI, non-streaming)

```bash
curl -s -X POST "$BASE/v1/chat/completions" \
  -H "Authorization: Bearer $GW_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"Halo"}],"max_tokens":512}'
```

## Login admin (tenant atau platform) dan pakai JWT

```bash
TOKEN=$(curl -s -X POST "$BASE/admin/api/auth/login" -H "Content-Type: application/json" \
  -d '{"email":"admin@example.com","password":"<password>"}' | jq -r .accessToken)

curl -s "$BASE/admin/api/projects" -H "Authorization: Bearer $TOKEN"
```

## Catatan

- Header `X-Request-Id` dikembalikan di setiap respons data plane; kirim `X-Gateway-Tags` untuk menandai request.
- Error berformat `{"error":{"message","type","param","code"}}`; `429` menyertakan `Retry-After`.
- Streaming belum didukung (`stream: true` ditolak `400 streaming_not_supported`).
