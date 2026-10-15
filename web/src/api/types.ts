export interface ConversationDto {
  id: string
  title: string | null
  createdAt: string
  /** Hanya lower bound bila usageIncomplete; ada panggilan AI yang tidak melaporkan usage. */
  tokensUsed: number
  usageIncomplete: boolean
  /** Model terakhir yang dipakai percakapan ini; null untuk riwayat lama (wajib pilih model sebelum kirim). */
  modelAlias: string | null
}

export interface AttachmentDto {
  id: string
  fileName: string
  kind: string
  sizeBytes: number
  downloadUrl: string
  /** Content-Type kanonik dari server (bukan MIME kiriman user); hanya PNG/JPEG/WEBP kanonik yang dipratinjau. */
  contentType: string
}

/**
 * Kebijakan lampiran efektif dari server (`GET /api/attachment-policy`): sumber tunggal batas unggah dan kirim,
 * jadi UI tidak pernah menampilkan angka hardcoded yang menyimpang dari yang ditegakkan API.
 */
export interface AttachmentPolicyDto {
  maxBytes: number
  /** Ekstensi kanonik huruf kecil (mis. '.pdf'); video tidak pernah ada di sini. */
  allowedExtensions: string[]
  maxAttachmentsPerMessage: number
  /** Batas dimensi gambar (bukan byte) yang didecode worker; informasi, bukan batas unggahan. */
  maxImagePixels: number
  /** Versi snapshot pengaturan gateway yang berlaku; null = baseline lokal. */
  sourceSettingsVersion: string | null
  policyHash: string
}

export interface MessageDto {
  id: number
  role: 'user' | 'assistant'
  content: string | null
  createdAt: string
  attachments: AttachmentDto[]
  /** Finish reason apa adanya dari penyedia AI (mis. 'length'); tidak pernah berisi reasoning. */
  finishReason: string | null
  /** Model yang menghasilkan pesan ini; badge memakai nilai ini, bukan pilihan model yang aktif sekarang. */
  modelAlias: string | null
  /** Stream draft yang digantikan pesan ini; null untuk balasan utuh (non-stream) dan pesan lama. */
  streamId: string | null
  /** Tiket yang menulis baris ini; null untuk pesan user. Dipakai menutup indikator tunggu tiket itu. */
  queueItemId: number | null
  /** Pesan user yang dijawab baris ini (dari tiket penulisnya); null untuk pesan user dan baris lama. */
  sourceMessageId: number | null
  /** false = jawaban alternatif (Jawab ulang/Bandingkan), bukan jawaban utama giliran itu. */
  isPrimary: boolean
  /** Pemakaian token jawaban ini; null bila baris lama atau penyedia tidak melaporkan usage. */
  tokensUsed: number | null
  /** Waktu tunggu di antrean sampai tiket diambil worker. */
  queueWaitMs: number | null
  /** Waktu sampai potongan pertama jawaban; null untuk jawaban utuh (non-stream) dan baris lama. */
  firstTokenMs: number | null
  /** Waktu proses tiket sampai jawaban akhir tersimpan. */
  totalMs: number | null
}

/** Tiket yang masih Pending/Processing: sumber status tunggu, bukan tebakan dari pesan terakhir. */
export interface ActivityDto {
  queueItemId: number
  status: 'Pending' | 'Processing'
  /** Pesan yang jadi acuan tiket ini (untuk jawab ulang/bandingkan); null bila belum diketahui. */
  sourceMessageId: number | null
  /** Alias model tiket ini; null bila belum diketahui (mis. baris optimistis sebelum snapshot server). */
  modelAlias: string | null
  type: string
  /** true = tiket alternatif (Jawab ulang/Bandingkan); indikator tunggu utama tidak menghitungnya. */
  isAlternative: boolean
}

/** 202 POST regenerate: tiket jawab ulang. `modelAlias` = model jawaban yang diulang. */
export interface TurnEnqueued {
  queueItemId: number
  sourceMessageId: number
  modelAlias: string | null
}

/** 202 POST compare: satu tiket per model. Cabang gagal dilaporkan per model sebelum apa pun diantrekan. */
export interface CompareEnqueued {
  sourceMessageId: number
  branches: { queueItemId: number; modelAlias: string }[]
}

/** GET/POST /api/collection: snapshot jawaban yang disimpan user. */
export interface SavedAnswer {
  id: string
  content: string
  modelAlias: string | null
  sessionTitle: string | null
  conversationId: string | null
  messageId: number | null
  createdAt: string
  /** false = sesi sumbernya sudah dihapus; isi jawaban tetap ada. */
  sourceAvailable: boolean
}

/**
 * Batch draft streaming worker lewat SignalR `streamEvent`.
 * `text` hanya teks tambahan (bukan seluruh buffer); `sequence`/`offset` menjaga chunk duplikat/gap.
 * offset dalam karakter UTF-16, sama dengan panjang string JavaScript.
 */
export interface StreamEvent {
  conversationId: string
  queueItemId: number
  runId: string
  streamId: string
  /** Panggilan AI ke berapa dalam tiket ini, mulai 0. */
  round: number
  kind: 'started' | 'delta' | 'aborted'
  sequence: number
  offset: number
  text: string | null
}

/** Katalog dari gateway; kunci hanya ada di server. */
export interface ModelInfoDto {
  id: string
  /** null = belum dikonfirmasi; tools null/false tetap tampil tapi tidak bisa dipilih. */
  supportsTools: boolean | null
  /** true = jawaban dialirkan sebagai draft; false/null = jawaban utuh. Tidak memengaruhi kelayakan memilih. */
  supportsStreaming: boolean | null
  ready: boolean
  maxOutputTokens: number | null
  /** true = bisa memeriksa gambar (PNG/JPEG/WEBP) dan PDF hasil scan; null = belum dikonfirmasi; false = tidak menerima gambar. */
  supportsVision: boolean | null
  /** Alasan dari gateway saat ready=false (mis. 'readiness_unknown'). */
  unavailableReason: string | null
}

/** 202 POST /messages: id tiket antrean ikut dikembalikan bersama pesannya. */
export interface SendMessageResponse {
  message: MessageDto
  queueItemId: number
}

export interface JobDto {
  id: number
  jobType: 'ExcelRead' | 'Analyze' | 'Pdf' | 'DocumentRead' | 'DocumentAnalyze'
  status: 'Pending' | 'Processing' | 'Done' | 'Failed'
  error: string | null
  createdAt: string
}
