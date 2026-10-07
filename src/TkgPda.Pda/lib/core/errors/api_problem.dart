class ApiProblem implements Exception {
  const ApiProblem({
    required this.status,
    required this.code,
    required this.detail,
    required this.correlationId,
  });

  final int status;
  final String code;
  final String detail;
  final String? correlationId;

  bool get isUnauthorized => status == 401;
  bool get shouldRefetch => status == 409 || status == 422 || status == 428;

  String get operatorMessage => switch (code) {
    'PRODUCTION_SESSION_CHANGED' || 'PRODUCTION_CONTEXT_MISMATCH' =>
      'Üretim bilgileri değişti. Durumu yenileyin; talep gönderilmedi.',
    'PRODUCTION_SESSION_ALREADY_ACTIVE' =>
      'Bu makinede zaten açık bir üretim var.',
    'PRODUCTION_SESSION_ACTIVE' => 'Önce açık üretimi bitirin.',
    'PRODUCTION_ACCESS_DENIED' =>
      'Bu makinede üretim işlemi yetkiniz bulunmuyor.',
    'BRING_EMPTY_SLOT_NOT_FOUND' => 'Bu parça için boş konum bulunamadı.',
    'REMOVE_FULL_SLOT_NOT_FOUND' => 'Bu parça için dolu kasa bulunamadı.',
    'REMOVE_EMPTY_SLOT_NOT_FOUND' => 'Bu parça için boş kasa bulunamadı.',
    'ACTIVE_TRANSPORT_EXISTS' =>
      'Bu işlem için aktif bir taşıma talebi zaten var.',
    'PART_REQUEST_ALREADY_ACTIVE' =>
      'Bu alt parça için aktif talep zaten var.',
    'PART_AREA_STATE_UNKNOWN' =>
      'Alan durumu bilinmiyor. Boşaltma veya dolu getirme tahmini yapılmadı.',
    'PART_NOT_ALT_PART' =>
      'Seçilen parça aktif üretimin alt parçası değil.',
    'SLOT_ALREADY_RESERVED' || 'SLOT_BUSY' =>
      'Uygun konum başka bir taşıma için kullanılıyor. Durumu yenileyin.',
    'RUNTIME_CONTEXT_MISMATCH' ||
    'VARIANT_LAYOUT_CONFLICT' ||
    'PART_CONFLICT' =>
      'Makine bilgisi değişti. Durumu yenileyip tekrar deneyin.',
    'MACHINE_ACCESS_DENIED' || 'MACHINE_OPERATION_NOT_PERMITTED' =>
      'Bu makinede taşıma işlemi yetkiniz bulunmuyor.',
    'DEVICE_MACHINE_BINDING_REQUIRED' =>
      'Bu terminal henüz bir makineye tanımlanmamış. Lütfen sistem yöneticisine başvurun.',
    'DEVICE_MACHINE_SCOPE_MISMATCH' =>
      'Bu terminal yalnızca tanımlı olduğu makine üzerinde işlem yapabilir.',
    'ACTIVE_SESSION_EXISTS' =>
      'Bu sicil başka bir cihazda aktif. Önce mevcut oturumdan çıkış yapın veya oturumun düşmesini bekleyin.',
    'RUNTIME_STATE_NOT_CONFIGURED' =>
      'Bu makinenin aktif varyant ve yerleşim bilgisi henüz hazırlanmadı. Sistem yöneticisine başvurun.',
    'NAVIGATION_PROFILE_AMBIGUOUS' =>
      'Birden fazla harita tanımı bulundu ve varsayılan belirlenmedi. Sistem yöneticisine başvurun.',
    'NAVIGATION_PROFILE_CONFLICT' =>
      'Seçilen harita profili bu varyant ile uyumlu değil.',
    'NAVIGATION_CONTEXT_MISMATCH' =>
      'Harita profili makinenin aktif profiliyle uyuşmuyor.',
    'PRODUCTION_ENDING' || 'PRODUCTION_END_IN_PROGRESS' =>
      'Üretim bitiriliyor. Yeni işlem başlatılamaz.',
    'PRODUCTION_END_PRIORITY_NOT_CONFIGURED' =>
      'Üretim bitiş önceliği henüz tanımlanmamış. Sistem yöneticisine başvurun.',
    'PRODUCTION_END_REMOVAL_NOT_CONFIGURED' =>
      'Üretim bitişi boşaltma alanı tanımlanmamış. Sistem yöneticisine başvurun.',
    'SLOT_NOT_IN_ACTIVE_LAYOUT' ||
    'CROSS_MACHINE_SLOT' ||
    'CROSS_LAYOUT_SLOT' =>
      'Seçilen konum makinenin aktif yerleşimine ait değil.',
    'SLOT_DISABLED' =>
      'Seçilen konum kullanım dışı.',
    'OCCUPIED_SLOT_REQUIRED' =>
      'Yalnızca dolu konumlar için doluluk durumu bildirilebilir.',
    'LOAD_STATE_NOT_SELECTABLE' =>
      'Bu doluluk durumu seçilemez.',
    'SESSION_NOT_ACTIVE' || 'SESSION_EXPIRED' =>
      'Oturumunuz sona erdi. Lütfen yeniden giriş yapın.',
    _ => switch (status) {
      400 => 'Gönderilen bilgiler geçersiz.',
      401 => 'Oturumunuz sona erdi. Lütfen yeniden giriş yapın.',
      403 => 'Bu işlem için yetkiniz bulunmuyor.',
      404 => 'İstenen kayıt bulunamadı.',
      409 => 'İşlem mevcut durumla çakışıyor. Durumu yenileyin.',
      422 => 'İşlem mevcut saha durumuna uygun değil.',
      428 => 'İşlem için güncel sürüm bilgisi gerekli.',
      429 => 'Çok fazla istek gönderildi. Kısa süre sonra tekrar deneyin.',
      503 => 'Sistem geçici olarak kullanılamıyor.',
      _ => 'İşlem tamamlanamadı. Lütfen tekrar deneyin.',
    },
  };

  @override
  String toString() => 'ApiProblem($status, $code)';
}

class ConnectivityException implements Exception {
  const ConnectivityException([
    this.message = 'Backend bağlantısı kurulamadı.',
  ]);

  final String message;
}

class AmbiguousMutationException implements Exception {
  const AmbiguousMutationException({
    required this.idempotencyKey,
    this.message = 'İsteğin sunucuya ulaşıp ulaşmadığı doğrulanamadı.',
  });

  final String idempotencyKey;
  final String message;
}
