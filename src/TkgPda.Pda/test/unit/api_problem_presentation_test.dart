import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';

void main() {
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
      detail: 'Machine runtime state must exist before changing workflow state.',
      correlationId: null,
    );

    expect(
      problem.operatorMessage,
      'Bu makinenin aktif varyant ve yerleşim bilgisi henüz hazırlanmadı. Sistem yöneticisine başvurun.',
    );
  });

  test('navigation and production configuration errors map to descriptive messages', () {
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
  });
}
