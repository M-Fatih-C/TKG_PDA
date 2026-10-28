import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';

void main() {
  test('Container state and capacity failures have actionable operator messages', () {
    const messages = {
      'ACTIVE_LAYOUT_REQUIRED':
          'Makinenin etkin yerleşimi tanımlı değil. Sistem yöneticisine başvurun.',
      'PART_AREA_AMBIGUOUS':
          'Alt parça birden fazla dolu konumda görünüyor. Konum bilgisini kontrol ettirin.',
      'PART_AREA_CURRENT_PART_UNKNOWN':
          'Konumdaki parçanın bilgisi bilinmiyor. Durumu kontrol edip yenileyin.',
      'PART_AREA_OCCUPIED_BY_OTHER_PART':
          'Konumda başka bir parça bulunuyor. Farklı palet kaldırılmadı.',
      'PART_AREA_STATE_CONFLICT':
          'Malzeme konumunun durumu çelişkili. Durumu kontrol edip yenileyin.',
      'PART_SLOT_NOT_CONFIGURED':
          'Bu parçanın makine konumu eşleşmemiş. Sistem yöneticisine başvurun.',
      'PART_SLOT_CONTAINER_MISMATCH':
          'Parçanın kasa tipi ile makine konumu uyuşmuyor. Sistem yöneticisine başvurun.',
      'CONTAINER_CONFLICT':
          'Parçanın kasa tipi ile makine konumu uyuşmuyor. Sistem yöneticisine başvurun.',
      'PART_DEMAND_DUPLICATE': 'Aynı alt parçayı toplu talepte bir kez seçin.',
      'PRODUCTION_SESSION_NOT_FOUND':
          'Bu makinede açık üretim bulunamadı. Üretim durumunu yenileyin.',
      'PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE':
          'Üretim bitişi için yeterli boş dönüş alanı bulunamadı.',
      'REMOVE_ALL_NOTHING_TO_REMOVE': 'Kaldırılacak kasa bulunmuyor.',
      'REMOVE_ALL_POSITION_UNKNOWN':
          'En az bir kasa konumunun durumu bilinmiyor. Durumu kontrol ettirin.',
      'REMOVE_ALL_POSITION_CONFLICT':
          'Kasa konumlarının durumları çelişkili. Durumu kontrol ettirin.',
      'REMOVE_ALL_NOT_READY':
          'Bazı kasa konumları kaldırma işlemine uygun değil. Konum durumlarını kontrol edin.',
      'EMPTY_BUFFER_SLOT_NOT_CONFIGURED':
          'Boş kasa saklama alanı tanımlı değil. Sistem yöneticisine başvurun.',
      'EMPTY_BUFFER_STATE_MISMATCH':
          'Boş kasa alanının durumu değişti. Durumu yenileyin.',
      'EMPTY_PALLET_HAS_MATERIAL':
          'Boş olarak belirtilen kasada malzeme bilgisi var. Kasa içeriğini kontrol edin.',
      'FILLED_SUPPLY_POSITION_NOT_VACANT':
          'Malzeme konumu boş değil. Durumu kontrol edip yenileyin.',
      'FILLED_SUPPLY_POSITION_RESERVED':
          'Malzeme konumu başka bir talep için kullanılıyor. Durumu yenileyin.',
      'PREPARATION_AREA_ALLOCATED':
          'Hazırlık alanı başka bir talep için ayrılmış. Durumu yenileyin.',
      'PREPARATION_AREA_NOT_VACANT':
          'Hazırlık alanı dolu. Uygun boş hazırlık alanını kullanın.',
      'PREPARATION_DEMAND_ALREADY_CLAIMED':
          'Bu talebin hazırlığı zaten üstlenilmiş. Talep durumunu yenileyin.',
      'PREPARATION_DEMAND_INCOMPLETE':
          'Talebin parça veya kasa bilgisi eksik. Sistem yöneticisine başvurun.',
      'PREPARATION_LOAD_UNKNOWN':
          'Hazırlanan kasanın boş/dolu durumu bilinmiyor. İçeriğini kontrol edin.',
      'PREPARATION_LOAD_MISMATCH':
          'Hazırlanan kasanın boş/dolu durumu taleple uyuşmuyor.',
      'PREPARATION_CONTAINER_MISMATCH':
          'Hazırlanan kasa tipi taleple uyuşmuyor.',
      'PREPARATION_PART_MISMATCH': 'Hazırlanan parça taleple uyuşmuyor.',
      'PREPARATION_PALLET_CHANGED': 'Hazırlık kaydı değişti. Durumu yenileyin.',
      'PREPARATION_PALLET_NOT_PLACED':
          'Hazır onayından önce kasanın hazırlık alanına yerleştirildiğini doğrulayın.',
      'PREPARATION_PALLET_NOT_READY': 'Kasanın hazırlığı henüz onaylanmamış.',
      'PREPARATION_PALLET_REQUIRED':
          'İşlem için bir hazırlık kasası seçilmelidir.',
      'PREPARATION_PALLET_NOT_FOUND':
          'Hazırlık kasası bulunamadı. Durumu yenileyin.',
      'PREPARATION_DEMAND_REQUIRED':
          'Hazırlanan kasayı bir taşıma talebiyle eşleştirin.',
      'PREPARATION_REQUEST_NOT_WAITING':
          'Talep artık hazırlık beklemiyor. Talep durumunu yenileyin.',
      'PREPARATION_READINESS_CONFLICT':
          'Hazırlık durumu değişti. Durumu yenileyin.',
      'PREPARATION_VERSION_CONFLICT':
          'Hazırlık kaydı başka bir işlemle değişti. Durumu yenileyin.',
      'STORE_VERSION_CONFLICT':
          'Depo alanının durumu değişti. Durumu yenileyin.',
      'REQUEST_TERMINAL':
          'Bu taşıma talebi artık aktif değil. Durumu yenileyin.',
      'CANCELLATION_NOT_ALLOWED':
          'Bu taşıma talebi mevcut durumda iptal edilemiyor.',
      'IDEMPOTENCY_KEY_REUSED':
          'İstek önceki işlemle uyuşmuyor. Durumu yenileyip tekrar deneyin.',
      'CONTAINER_STATE_CONFLICT':
          'Kasanın mevcut durumu bu işlemle uyuşmuyor. Durumu yenileyin.',
      'CONTAINER_POSITION_NOT_FOUND':
          'Kasa konumu etkin üretimde bulunamadı. Üretim durumunu yenileyin.',
      'SLOT_STATE_MISMATCH': 'Konumun durumu değişti. Durumu yenileyin.',

      'CONTAINER_POSITION_OCCUPIED': 'Bu konumda zaten bir kasa bulunuyor.',
      'OCCUPIED_CONTAINER_REQUIRED':
          'Dolu bildirimi için konumda bir kasa bulunmalıdır.',
      'BUSINESS_RULE_NOT_CONFIGURED':
          'Kasa konumunun durumu bilinmiyor. Durumu kontrol edip yenileyin.',
      'FINISHED_GOODS_CAPACITY_UNAVAILABLE':
          'Uygun boş bitmiş ürün alanı bulunamadı.',
      'NON_FINISHED_RETURN_CAPACITY_UNAVAILABLE':
          'Uygun boş yarı mamul dönüş alanı bulunamadı.',
      'EMPTY_RETURN_CAPACITY_UNAVAILABLE':
          'Uygun boş kasa dönüş alanı bulunamadı.',
      'SHARED_ENDPOINT_CAPACITY_UNAVAILABLE':
          'Uygun boş hedef alan bulunamadı.',
      'RDS_WORKSITE_LOCKED':
          'Fiziksel robot noktası kilitli. Alanın serbest bırakılmasını bekleyip ekranı yenileyin.',
      'RDS_WORKSITE_STALE':
          'Fiziksel robot noktasının kanıtı güncel değil. RDS senkronunu kontrol edip ekranı yenileyin.',
      'RDS_WORKSITE_NOT_PHYSICAL':
          'Eşlenen robot noktası fiziksel değil. Sistem yöneticisine başvurun.',
      'RDS_WORKSITE_OCCUPANCY_CONFLICT':
          'Fiziksel alan ile sistem doluluk bilgisi uyuşmuyor. Alanı doğrulatıp RDS senkronundan sonra yeniden deneyin.',
      'RDS_WORKSITE_STATE_UNKNOWN':
          'Fiziksel alanın durumu bilinmiyor. RDS senkronunu kontrol edip ekranı yenileyin.',
      'RDS_WORKSITE_DISABLED':
          'Robot çalışma noktası kullanım dışı. Sistem yöneticisine başvurun.',
      'RDS_WORKSITE_SYNC_FAILED':
          'Robot çalışma noktası senkronize değil. Sistem yöneticisine başvurun.',
    };
    for (final entry in messages.entries) {
      expect(
        ApiProblem(
          status: 422,
          code: entry.key,
          detail: 'internal detail',
          correlationId: null,
        ).operatorMessage,
        entry.value,
      );
    }
  });

  test('known slot-resolution failures use concise operational Turkish', () {
    const problem = ApiProblem(
      status: 422,
      code: 'BRING_EMPTY_SLOT_NOT_FOUND',
      detail: 'WorkflowRuleException: destinationSlotId has no WorkSite.',
      correlationId: '00000000-0000-0000-0000-000000000001',
    );

    expect(problem.operatorMessage, 'Bu parça için boş konum bulunamadı.');
    expect(problem.operatorMessage, isNot(contains('WorkflowRuleException')));
    expect(problem.operatorMessage, isNot(contains('WorkSite')));
  });

  test('unknown conflicts never expose server or database detail', () {
    const problem = ApiProblem(
      status: 409,
      code: 'UNRECOGNIZED_CONFLICT',
      detail: 'database row sourceSlotId conflict for UUID 123',
      correlationId: null,
    );

    expect(
      problem.operatorMessage,
      'İşlem mevcut durumla çakışıyor. Durumu yenileyin.',
    );
    expect(problem.operatorMessage, isNot(contains('sourceSlotId')));
    expect(problem.operatorMessage, isNot(contains('database')));
  });

  test('RUNTIME_STATE_NOT_CONFIGURED maps to descriptive operator guidance', () {
    const problem = ApiProblem(
      status: 422,
      code: 'RUNTIME_STATE_NOT_CONFIGURED',
      detail:
          'Machine runtime state must exist before changing workflow state.',
      correlationId: null,
    );

    expect(
      problem.operatorMessage,
      'Bu makinenin aktif varyant ve yerleşim bilgisi henüz hazırlanmadı. Sistem yöneticisine başvurun.',
    );
  });

  test(
    'navigation and production configuration errors map to descriptive messages',
    () {
      const navProblem = ApiProblem(
        status: 409,
        code: 'NAVIGATION_PROFILE_AMBIGUOUS',
        detail: 'More than one published navigation profile is assigned.',
        correlationId: null,
      );
      expect(
        navProblem.operatorMessage,
        'Birden fazla harita tanımı bulundu ve varsayılan belirlenmedi. Sistem yöneticisine başvurun.',
      );

      const endingProblem = ApiProblem(
        status: 409,
        code: 'PRODUCTION_ENDING',
        detail: 'Production is ending.',
        correlationId: null,
      );
      expect(
        endingProblem.operatorMessage,
        'Üretim bitiriliyor. Yeni işlem başlatılamaz.',
      );
    },
  );
}
