using System.Text.Json.Nodes;

namespace OperatorAi.Worker.Operator.Tools;

/// <summary>Definisi tools untuk field <c>tools</c> (format OpenAI function calling; DESIGN bagian 9).</summary>
public static class ToolDefinitions
{
    public const string AnalisisFile = "analisis_file", AnalisisDokumen = "analisis_dokumen",
        BuatLaporanPdf = "buat_laporan_pdf", CekStatus = "cek_status", CariWeb = "cari_web",
        FetchUrl = "fetch_url";

    public static JsonArray All() => (JsonArray)JsonNode.Parse("""
        [
          {
            "type": "function",
            "function": {
              "name": "analisis_file",
              "description": "Membaca file Excel/CSV yang diupload user lalu menganalisisnya: ringkasan isi, masalah data, dan rekomendasi digitalisasi. Gunakan bila user meminta ringkasan, analisis, atau saran dari file yang dilampirkan.",
              "parameters": {
                "type": "object",
                "properties": {
                  "attachment_id": { "type": "string", "description": "attachment_id dari lampiran user" },
                  "tujuan": { "type": "string", "description": "Apa yang user ingin ketahui atau capai dari data ini, dalam kalimat user" },
                  "buat_pdf": { "type": "boolean", "description": "true bila user juga meminta laporan PDF" },
                  "langsung": { "type": "boolean", "description": "true bila user cukup diberi hasil laporan apa adanya; false/absen bila jawaban perlu diterjemahkan, diringkas, atau disusun ulang" }
                },
                "required": ["attachment_id", "tujuan", "buat_pdf"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "analisis_dokumen",
              "description": "Membaca dan menganalisis PDF atau gambar PNG/JPEG/WEBP yang dilampirkan user. Gunakan untuk ringkasan, pertanyaan isi dokumen, scan, screenshot, grafik, diagram, atau foto. Isi file tidak tersedia sebelum job selesai. Bukan untuk video atau membuat diagram baru.",
              "parameters": {
                "type": "object",
                "properties": {
                  "attachment_id": { "type": "string", "description": "attachment_id lampiran PDF atau gambar" },
                  "tujuan": { "type": "string", "description": "Pertanyaan atau tujuan user atas dokumen/gambar" },
                  "visual": { "type": "boolean", "description": "true bila pertanyaan membutuhkan grafik, gambar, diagram, atau tata letak PDF; scan/gambar otomatis memakai vision" },
                  "langsung": { "type": "boolean", "description": "true bila hasil analisis boleh disampaikan lengkap apa adanya; false bila perlu sintesis dengan sumber lain" }
                },
                "required": ["attachment_id", "tujuan"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "buat_laporan_pdf",
              "description": "Membuat laporan PDF dari hasil analisis yang sudah selesai sebelumnya di percakapan ini.",
              "parameters": {
                "type": "object",
                "properties": {
                  "judul": { "type": "string" },
                  "langsung": { "type": "boolean", "description": "true bila user cukup diberi lampiran PDF-nya apa adanya; false/absen bila isinya perlu dijelaskan ulang di chat" }
                },
                "required": ["judul"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "cek_status",
              "description": "Melihat status pekerjaan yang sedang berjalan di percakapan ini. Gunakan bila user bertanya progres.",
              "parameters": { "type": "object", "properties": {} }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "cari_web",
              "description": "Mencari informasi terkini dari internet melalui layanan pencarian web aplikasi. Gunakan bila user meminta informasi terkini atau saat pencarian web relevan. Mengembalikan judul, ringkasan, dan URL sumber untuk sitasi.",
              "parameters": {
                "type": "object",
                "properties": {
                  "kueri": { "type": "string", "description": "Kueri pencarian yang jelas dan spesifik (maksimal 200 karakter)" },
                  "jumlah": { "type": "integer", "description": "Jumlah hasil pencarian yang diinginkan (1 sampai 5, bawaan 3)" }
                },
                "required": ["kueri"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "fetch_url",
              "description": "Mengambil isi teks halaman dari sebuah URL publik (http/https). Gunakan bila hasil pencarian belum cukup atau user memberi tautan. Hanya alamat publik yang dilayani.",
              "parameters": {
                "type": "object",
                "properties": {
                  "url": { "type": "string", "description": "Alamat URL lengkap halaman publik yang ingin dibaca (harus diawali http:// atau https://)" }
                },
                "required": ["url"]
              }
            }
          }
        ]
        """)!;
}
