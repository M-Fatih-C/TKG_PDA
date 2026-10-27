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
    'PART_REQUEST_ALREADY_ACTIVE' => 'Bu alt parça için aktif talep zaten var.',
    'PART_AREA_STATE_UNKNOWN' =>
      'Alan durumu bilinmiyor. Boşaltma veya dolu getirme tahmini yapılmadı.',
    'PART_NOT_ALT_PART' => 'Seçilen parça aktif üretimin alt parçası değil.',
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
    'CONTAINER_TYPE_MISSING' => 'Taşıma kabı bilgisi bekleniyor.',
    'ACTIVE_LAYOUT_REQUIRED' =>
      'Makinenin etkin yerleşimi tanımlı değil. Sistem yöneticisine başvurun.',
    'PART_AREA_AMBIGUOUS' =>
      'Alt parça birden fazla dolu konumda görünüyor. Konum bilgisini kontrol ettirin.',
    'PART_AREA_CURRENT_PART_UNKNOWN' =>
      'Konumdaki parçanın bilgisi bilinmiyor. Durumu kontrol edip yenileyin.',
    'PART_AREA_OCCUPIED_BY_OTHER_PART' =>
      'Konumda başka bir parça bulunuyor. Farklı palet kaldırılmadı.',
    'PART_AREA_STATE_CONFLICT' =>
      'Malzeme konumunun durumu çelişkili. Durumu kontrol edip yenileyin.',
    'PART_SLOT_NOT_CONFIGURED' =>
      'Bu parçanın makine konumu eşleşmemiş. Sistem yöneticisine başvurun.',
    'PART_SLOT_CONTAINER_MISMATCH' || 'CONTAINER_CONFLICT' =>
      'Parçanın kasa tipi ile makine konumu uyuşmuyor. Sistem yöneticisine başvurun.',
    'PART_DEMAND_DUPLICATE' => 'Aynı alt parçayı toplu talepte bir kez seçin.',
    'PRODUCTION_SESSION_NOT_FOUND' =>
      'Bu makinede açık üretim bulunamadı. Üretim durumunu yenileyin.',
    'PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE' =>
      'Üretim bitişi için yeterli boş dönüş alanı bulunamadı.',
    'REMOVE_ALL_NOTHING_TO_REMOVE' => 'Kaldırılacak kasa bulunmuyor.',
    'REMOVE_ALL_POSITION_UNKNOWN' =>
      'En az bir kasa konumunun durumu bilinmiyor. Durumu kontrol ettirin.',
    'REMOVE_ALL_POSITION_CONFLICT' =>
      'Kasa konumlarının durumları çelişkili. Durumu kontrol ettirin.',
    'REMOVE_ALL_NOT_READY' =>
      'Bazı kasa konumları kaldırma işlemine uygun değil. Konum durumlarını kontrol edin.',
    'EMPTY_BUFFER_SLOT_NOT_CONFIGURED' =>
      'Boş kasa saklama alanı tanımlı değil. Sistem yöneticisine başvurun.',
    'EMPTY_BUFFER_STATE_MISMATCH' =>
      'Boş kasa alanının durumu değişti. Durumu yenileyin.',
    'EMPTY_PALLET_HAS_MATERIAL' =>
      'Boş olarak belirtilen kasada malzeme bilgisi var. Kasa içeriğini kontrol edin.',
    'FILLED_SUPPLY_POSITION_NOT_VACANT' =>
      'Malzeme konumu boş değil. Durumu kontrol edip yenileyin.',
    'FILLED_SUPPLY_POSITION_RESERVED' =>
      'Malzeme konumu başka bir talep için kullanılıyor. Durumu yenileyin.',
    'PREPARATION_AREA_ALLOCATED' =>
      'Hazırlık alanı başka bir talep için ayrılmış. Durumu yenileyin.',
    'PREPARATION_AREA_NOT_VACANT' =>
      'Hazırlık alanı dolu. Uygun boş hazırlık alanını kullanın.',
    'PREPARATION_DEMAND_ALREADY_CLAIMED' =>
      'Bu talebin hazırlığı zaten üstlenilmiş. Talep durumunu yenileyin.',
    'PREPARATION_DEMAND_INCOMPLETE' =>
      'Talebin parça veya kasa bilgisi eksik. Sistem yöneticisine başvurun.',
    'PREPARATION_LOAD_UNKNOWN' =>
      'Hazırlanan kasanın boş/dolu durumu bilinmiyor. İçeriğini kontrol edin.',
    'PREPARATION_LOAD_MISMATCH' =>
      'Hazırlanan kasanın boş/dolu durumu taleple uyuşmuyor.',
    'PREPARATION_CONTAINER_MISMATCH' =>
      'Hazırlanan kasa tipi taleple uyuşmuyor.',
    'PREPARATION_PART_MISMATCH' => 'Hazırlanan parça taleple uyuşmuyor.',
    'PREPARATION_PALLET_CHANGED' => 'Hazırlık kaydı değişti. Durumu yenileyin.',
    'PREPARATION_PALLET_NOT_PLACED' =>
      'Hazır onayından önce kasanın hazırlık alanına yerleştirildiğini doğrulayın.',
    'PREPARATION_PALLET_NOT_READY' => 'Kasanın hazırlığı henüz onaylanmamış.',
    'PREPARATION_PALLET_REQUIRED' =>
      'İşlem için bir hazırlık kasası seçilmelidir.',
    'PREPARATION_PALLET_NOT_FOUND' =>
      'Hazırlık kasası bulunamadı. Durumu yenileyin.',
    'PREPARATION_DEMAND_REQUIRED' =>
      'Hazırlanan kasayı bir taşıma talebiyle eşleştirin.',
    'PREPARATION_REQUEST_NOT_WAITING' =>
      'Talep artık hazırlık beklemiyor. Talep durumunu yenileyin.',
    'PREPARATION_READINESS_CONFLICT' =>
      'Hazırlık durumu değişti. Durumu yenileyin.',
    'PREPARATION_VERSION_CONFLICT' =>
      'Hazırlık kaydı başka bir işlemle değişti. Durumu yenileyin.',
    'STORE_VERSION_CONFLICT' =>
      'Depo alanının durumu değişti. Durumu yenileyin.',
    'REQUEST_TERMINAL' =>
      'Bu taşıma talebi artık aktif değil. Durumu yenileyin.',
    'CANCELLATION_NOT_ALLOWED' =>
      'Bu taşıma talebi mevcut durumda iptal edilemiyor.',
    'IDEMPOTENCY_KEY_REUSED' =>
      'İstek önceki işlemle uyuşmuyor. Durumu yenileyip tekrar deneyin.',
    'CONTAINER_STATE_CONFLICT' =>
      'Kasanın mevcut durumu bu işlemle uyuşmuyor. Durumu yenileyin.',
    'CONTAINER_POSITION_NOT_FOUND' =>
      'Kasa konumu etkin üretimde bulunamadı. Üretim durumunu yenileyin.',
    'SLOT_STATE_MISMATCH' => 'Konumun durumu değişti. Durumu yenileyin.',

    'CONTAINER_POSITION_OCCUPIED' => 'Bu konumda zaten bir kasa bulunuyor.',
    'OCCUPIED_CONTAINER_REQUIRED' =>
      'Dolu bildirimi için konumda bir kasa bulunmalıdır.',
    'BUSINESS_RULE_NOT_CONFIGURED' =>
      'Kasa konumunun durumu bilinmiyor. Durumu kontrol edip yenileyin.',
    'FINISHED_GOODS_CAPACITY_UNAVAILABLE' =>
      'Uygun boş bitmiş ürün alanı bulunamadı.',
    'NON_FINISHED_RETURN_CAPACITY_UNAVAILABLE' =>
      'Uygun boş yarı mamul dönüş alanı bulunamadı.',
    'EMPTY_RETURN_CAPACITY_UNAVAILABLE' =>
      'Uygun boş kasa dönüş alanı bulunamadı.',
    'SHARED_ENDPOINT_CAPACITY_UNAVAILABLE' =>
      'Uygun boş hedef alan bulunamadı.',
    'RDS_WORKSITE_LOCKED' =>
      'Fiziksel robot noktası kilitli. Alanın serbest bırakılmasını bekleyip ekranı yenileyin.',
    'RDS_WORKSITE_STALE' =>
      'Fiziksel robot noktasının kanıtı güncel değil. RDS senkronunu kontrol edip ekranı yenileyin.',
    'RDS_WORKSITE_NOT_PHYSICAL' =>
      'Eşlenen robot noktası fiziksel değil. Sistem yöneticisine başvurun.',
    'RDS_WORKSITE_OCCUPANCY_CONFLICT' =>
      'Fiziksel alan ile sistem doluluk bilgisi uyuşmuyor. Alanı doğrulatıp RDS senkronundan sonra yeniden deneyin.',
    'RDS_WORKSITE_STATE_UNKNOWN' =>
      'Fiziksel alanın durumu bilinmiyor. RDS senkronunu kontrol edip ekranı yenileyin.',
    'RDS_WORKSITE_DISABLED' =>
      'Robot çalışma noktası kullanım dışı. Sistem yöneticisine başvurun.',
    'RDS_WORKSITE_SYNC_FAILED' =>
      'Robot çalışma noktası senkronize değil. Sistem yöneticisine başvurun.',
    'FINISHED_GOODS_DESTINATION_NOT_CONFIGURED' ||
    'FINISHED_GOODS_MAPPING_MISSING' =>
      'Bitmiş ürün teslim alanı henüz yapılandırılmamış. Sistem yöneticisine başvurun.',
    'EMPTY_SOURCE_MAPPING_MISSING' =>
      'Boş kasa alma alanı tanımlanmamış. Sistem yöneticisine başvurun.',
    'EMPTY_RETURN_MAPPING_MISSING' =>
      'Boş kasa iade alanı tanımlanmamış. Sistem yöneticisine başvurun.',
    'NON_FINISHED_RETURN_MAPPING_MISSING' =>
      'Yarı mamul iade alanı tanımlanmamış. Sistem yöneticisine başvurun.',
    'EMPTY_BUFFER_UNAVAILABLE' => 'Boş kasa için uygun boş saklama alanı yok.',
    'PRODUCTION_END_PART_UNKNOWN' =>
      'Konumdaki paletin parçası bilinmiyor. Üretim kapatılmadı.',
    'PRODUCTION_END_POSITION_UNKNOWN' || 'PRODUCTION_END_LOAD_UNKNOWN' =>
      'Konum veya yük durumu bilinmiyor. Üretim kapatılmadı.',
    'RDS_TASK_TEMPLATE_NOT_FOUND' =>
      'Bu işlem için aktif görev şablonu bulunamadı.',
    'RDS_WORKSITE_NOT_MAPPED' || 'RDS_WORKSITE_MAPPING_MISSING' =>
      'Robot çalışma noktası eşleşmesi bulunamadı.',
    'SLOT_NOT_IN_ACTIVE_LAYOUT' ||
    'CROSS_MACHINE_SLOT' ||
    'CROSS_LAYOUT_SLOT' =>
      'Seçilen konum makinenin aktif yerleşimine ait değil.',
    'SLOT_DISABLED' => 'Seçilen konum kullanım dışı.',
    'OCCUPIED_SLOT_REQUIRED' =>
      'Yalnızca dolu konumlar için doluluk durumu bildirilebilir.',
    'LOAD_STATE_NOT_SELECTABLE' => 'Bu doluluk durumu seçilemez.',
    'SESSION_NOT_ACTIVE' ||
    'SESSION_EXPIRED' => 'Oturumunuz sona erdi. Lütfen yeniden giriş yapın.',
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
