namespace OperatorAi.Shared.Storage;

/// <summary>
/// Isi file lampiran tidak memenuhi syarat dokumen: format tidak didukung, file rusak, terkunci password,
/// atau melewati batas halaman/piksel/karakter. Turunan <see cref="FormatException"/> supaya pemanggil yang sudah
/// menangani <see cref="FormatException"/> (mis. validasi unggah di API) tetap bekerja, sementara worker
/// memetakannya menjadi kegagalan permanen berkategori <c>documentInvalid</c> — percobaan ulang tidak akan berubah.
/// </summary>
public sealed class DocumentInputException(string message, Exception? inner = null) : FormatException(message, inner);
