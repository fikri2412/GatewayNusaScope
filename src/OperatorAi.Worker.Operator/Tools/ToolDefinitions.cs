using System.Text.Json.Nodes;

namespace OperatorAi.Worker.Operator.Tools;

/// <summary>Definisi tools untuk field <c>tools</c> (format OpenAI function calling; DESIGN bagian 9).</summary>
public static class ToolDefinitions
{
    public const string AnalisisFile = "analisis_file", AnalisisDokumen = "analisis_dokumen",
        BuatLaporanPdf = "buat_laporan_pdf", CekStatus = "cek_status", CariWeb = "cari_web",
        FetchUrl = "fetch_url", BuatArtifact = "buat_artifact", BacaArtifact = "baca_artifact",
        PerbaruiArtifact = "perbarui_artifact", BuatAlurLogika = "buat_alur_logika";

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
              "name": "buat_alur_logika",
              "description": "Membuat gambar alur logika (flowchart PNG) dari PRD: teks PRD di chat atau lampiran PDF bertext. Hasilnya lampiran PNG di chat, hanya dari yang tertulis di PRD; asumsi dan bagian yang belum dijelaskan dicatat terpisah. Untuk diagram yang akan diedit user pakai buat_artifact jenis diagram. Hasil tidak tersedia sebelum job selesai. Bukan untuk ilustrasi umum, PDF scan, atau gambar.",
              "parameters": {
                "type": "object",
                "properties": {
                  "judul": { "type": "string", "description": "Judul singkat diagram (maksimal 200 karakter); dipakai sebagai nama file" },
                  "tujuan": { "type": "string", "description": "Bagian atau alur apa dari PRD yang digambar, dalam kalimat user (mis. seluruh alur, hanya alur pembayaran)" },
                  "attachment_id": { "type": "string", "description": "attachment_id lampiran PDF berisi PRD; kosongkan bila PRD berupa teks di chat" },
                  "sumber_pesan": { "type": "string", "enum": ["ini", "sebelumnya"], "description": "Hanya untuk PRD teks: ini = PRD ada di pesan user ini (bawaan, minimal 200 karakter); sebelumnya = PRD ada di pesan user sebelumnya" },
                  "langsung": { "type": "boolean", "description": "true bila user cukup diberi gambar beserta catatannya apa adanya; false/absen bila hasilnya perlu dijelaskan atau disesuaikan di chat" }
                },
                "required": ["judul", "tujuan"]
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
          },
          {
            "type": "function",
            "function": {
              "name": "buat_artifact",
              "description": "Membuat dokumen, potongan kode, atau diagram Mermaid di panel Canvas (ruang kerja di samping chat) yang bisa diedit user. Gunakan untuk tulisan/kode yang panjang atau akan diedit ulang (laporan, draf, skrip), bukan untuk jawaban pendek. Isi penuh masuk ke Canvas; di chat cukup rangkum singkat apa yang dibuat.",
              "parameters": {
                "type": "object",
                "properties": {
                  "jenis": { "type": "string", "enum": ["dokumen", "kode", "diagram", "gambar"], "description": "dokumen = teks Markdown; kode = kode program; diagram = sumber Mermaid (flowchart, classDiagram, erDiagram, sequenceDiagram, dst.) yang dirender di Canvas; gambar = ilustrasi vektor berupa dokumen SVG lengkap (elemen akar <svg> dengan viewBox; tanpa script, foreignObject, atau atribut on*; tautan hanya #id lokal). Bukan foto atau gambar raster." },
                  "judul": { "type": "string", "description": "Judul singkat (maksimal 200 karakter)" },
                  "bahasa": { "type": "string", "description": "Hanya untuk jenis kode: nama bahasa huruf kecil, mis. csharp, python, sql, typescript" },
                  "isi": { "type": "string", "description": "Isi lengkap artifact (dokumen memakai Markdown; gambar berupa SVG)" },
                  "turunan_dari": { "type": "string", "description": "Opsional, untuk variasi: artifact_id sumber (berjenis sama) yang menjadi dasar artifact baru ini" },
                  "versi_sumber": { "type": "integer", "description": "Opsional: nomor versi sumber yang dijadikan dasar (bawaan: versi terbaru)" }
                },
                "required": ["jenis", "judul", "isi"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "baca_artifact",
              "description": "Membaca isi dan nomor versi terbaru sebuah artifact Canvas di percakapan ini. Wajib dipanggil sebelum perbarui_artifact bila user mungkin sudah mengeditnya, atau bila artifact_id/isi sebelumnya tidak kamu miliki.",
              "parameters": {
                "type": "object",
                "properties": {
                  "artifact_id": { "type": "string", "description": "artifact_id dari hasil buat_artifact atau dari daftar artifact" }
                },
                "required": ["artifact_id"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "perbarui_artifact",
              "description": "Menyimpan isi baru sebuah artifact Canvas sebagai versi baru. versi_dasar adalah nomor versi yang kamu baca/buat terakhir; bila user sudah mengubahnya, penyimpanan ditolak dan kamu harus baca ulang dengan baca_artifact lalu terapkan perubahan di atas isi terbaru.",
              "parameters": {
                "type": "object",
                "properties": {
                  "artifact_id": { "type": "string" },
                  "versi_dasar": { "type": "integer", "description": "Nomor versi yang menjadi dasar perubahanmu" },
                  "isi": { "type": "string", "description": "Isi lengkap yang baru (bukan diff)" }
                },
                "required": ["artifact_id", "versi_dasar", "isi"]
              }
            }
          }
        ]
        """)!;

    /// <summary>Tool yang hanya berguna bila ada file/hasil analisis; di mode <c>auto</c> disembunyikan dari percakapan tanpa lampiran.</summary>
    private static readonly HashSet<string> FileToolNames = [AnalisisFile, AnalisisDokumen, BuatLaporanPdf];

    public static JsonArray For(bool fileTools)
    {
        var all = All();
        if (fileTools) return all;
        var kept = new JsonArray();
        foreach (var tool in all.ToList())
            if (!FileToolNames.Contains((string)tool!["function"]!["name"]!))
                kept.Add(tool.DeepClone());
        return kept;
    }
}
