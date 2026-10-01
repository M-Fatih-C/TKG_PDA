import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../data/models/pda_models.dart';
import '../../shared/presentation.dart';

/// Customer UI — Multi-Part Transport Batch: the "Select Parts" and
/// "Review -> Submit Batch" steps of the wizard. Per-item variant/operation
/// configuration reuses the same-page [PartFirstSelector] action flow
/// (driven by [AppController.isConfiguringBatchItem]) and is therefore NOT
/// part of this file.
class MultiPartBatchWorkspace extends StatelessWidget {
  const MultiPartBatchWorkspace({required this.controller, super.key});

  final AppController controller;

  @override
  Widget build(BuildContext context) => controller.canReviewBatch
      ? _BatchReview(controller: controller)
      : _BatchPartBrowser(controller: controller);
}

class _BatchPartBrowser extends StatefulWidget {
  const _BatchPartBrowser({required this.controller});

  final AppController controller;

  @override
  State<_BatchPartBrowser> createState() => _BatchPartBrowserState();
}

class _BatchPartBrowserState extends State<_BatchPartBrowser> {
  MachineMainPartOption? _expanded;

  /// How many cart items came from this main part (itself or one of its
  /// RELATED/ALT parts) — surfaced as a small count badge on the browse
  /// chip so it is clear at a glance without expanding each one.
  int _cartCountFor(AppController controller, MachineMainPartOption main) =>
      controller.batchCart
          .where((item) => item.mainPart.partId == main.partId)
          .length;

  @override
  Widget build(BuildContext context) {
    final controller = widget.controller;
    return ListView(
      key: const Key('batch-part-browser'),
      padding: const EdgeInsets.all(14),
      children: [
        Row(
          children: [
            const Expanded(
              child: Text(
                'Birden Fazla Parça Seç',
                style: TextStyle(fontSize: 20, fontWeight: FontWeight.w900),
              ),
            ),
            IconButton(
              key: const Key('batch-exit'),
              tooltip: 'Grup Seçimini İptal Et',
              onPressed: controller.exitBatchSelection,
              icon: const Icon(Icons.close),
            ),
          ],
        ),
        const SizedBox(height: 6),
        const Text(
          'Gruba eklemek istediğiniz parçaları seçin. Başka bir ana parçaya '
          'göz atmak seçimi silmez.',
        ),
        const SizedBox(height: 14),
        _CartSummary(controller: controller),
        const SizedBox(height: 20),
        const Text(
          'Ana Parçalar',
          style: TextStyle(fontWeight: FontWeight.w800),
        ),
        const SizedBox(height: 8),
        LayoutBuilder(
          builder: (context, constraints) => Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final option in controller.partOptions)
                ConstrainedBox(
                  constraints: BoxConstraints(maxWidth: constraints.maxWidth),
                  child: ChoiceChip(
                    key: Key('batch-browse-main-${option.partId}'),
                    selected: _expanded?.partId == option.partId,
                    avatar: _cartCountFor(controller, option) > 0
                        ? CircleAvatar(
                            key: Key(
                              'batch-browse-main-count-${option.partId}',
                            ),
                            backgroundColor: const Color(0xFF166534),
                            child: Text(
                              '${_cartCountFor(controller, option)}',
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 12,
                                fontWeight: FontWeight.w800,
                              ),
                            ),
                          )
                        : null,
                    label: Text(
                      _partDisplay(option.partCode, option.description),
                      overflow: TextOverflow.ellipsis,
                      maxLines: 2,
                    ),
                    onSelected: (_) => setState(
                      () => _expanded = _expanded?.partId == option.partId
                          ? null
                          : option,
                    ),
                  ),
                ),
            ],
          ),
        ),
        if (_expanded case final main?) ...[
          const SizedBox(height: 16),
          _AddPartsCard(main: main, controller: controller),
        ],
        const SizedBox(height: 24),
        ElevatedButton.icon(
          key: const Key('batch-configure-start'),
          onPressed: controller.batchCart.isEmpty
              ? null
              : controller.beginBatchConfiguration,
          icon: const Icon(Icons.tune),
          label: const Text('Seçilenleri Yapılandır'),
        ),
        const SizedBox(height: 40),
      ],
    );
  }
}

class _CartSummary extends StatelessWidget {
  const _CartSummary({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final cart = controller.batchCart;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Seçili Parçalar: ${cart.length}',
              key: const Key('batch-cart-count'),
              style: const TextStyle(fontWeight: FontWeight.w900),
            ),
            if (cart.isEmpty)
              const Padding(
                padding: EdgeInsets.only(top: 8),
                child: Text('Henüz parça seçilmedi.'),
              )
            else ...[
              const SizedBox(height: 10),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final item in cart)
                    InputChip(
                      key: Key('batch-cart-chip-${item.effectivePart.partId}'),
                      label: Text(item.effectivePart.partCode),
                      onDeleted: () =>
                          controller.removePartFromBatch(item.cartItemId),
                      deleteIcon: const Icon(Icons.close, size: 18),
                    ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _AddPartsCard extends StatelessWidget {
  const _AddPartsCard({required this.main, required this.controller});

  final MachineMainPartOption main;
  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final related = main.relatedParts
        .where((part) => part.relationType == 'RELATED')
        .toList(growable: false);
    final alt = main.relatedParts
        .where((part) => part.relationType == 'ALT_PART')
        .toList(growable: false);
    final isMainInCart = controller.batchCart.any(
      (item) => item.effectivePart.partId == main.partId,
    );

    return Card(
      key: Key('batch-add-card-${main.partId}'),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Parça · ${main.partCode}',
              style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
            ),
            const SizedBox(height: 10),
            _AddPartChip(
              key: Key('batch-add-main-${main.partId}'),
              label: 'Ana Parçayı Ekle · ${main.partCode}',
              alreadyAdded: isMainInCart,
              onAdd: () => controller.addPartToBatch(
                main,
                EffectivePartSelection(
                  partId: main.partId,
                  partCode: main.partCode,
                  isMain: true,
                ),
              ),
            ),
            if (related.isNotEmpty) ...[
              const SizedBox(height: 14),
              const Text(
                'İlgili Parçalar',
                style: TextStyle(fontWeight: FontWeight.w800),
              ),
              const SizedBox(height: 8),
              _AddRelatedPartChips(
                parts: related,
                keyPrefix: 'related',
                main: main,
                controller: controller,
              ),
            ],
            if (alt.isNotEmpty) ...[
              const SizedBox(height: 14),
              const Text(
                'Alt Parçalar',
                style: TextStyle(fontWeight: FontWeight.w800),
              ),
              const SizedBox(height: 8),
              _AddRelatedPartChips(
                parts: alt,
                keyPrefix: 'alt',
                main: main,
                controller: controller,
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _AddRelatedPartChips extends StatelessWidget {
  const _AddRelatedPartChips({
    required this.parts,
    required this.keyPrefix,
    required this.main,
    required this.controller,
  });

  final List<MachineRelatedPart> parts;
  final String keyPrefix;
  final MachineMainPartOption main;
  final AppController controller;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        for (final part in parts)
          ConstrainedBox(
            constraints: BoxConstraints(maxWidth: constraints.maxWidth),
            child: _AddPartChip(
              key: Key('batch-add-$keyPrefix-${part.partId}'),
              label: part.partCode,
              alreadyAdded: controller.batchCart.any(
                (item) => item.effectivePart.partId == part.partId,
              ),
              onAdd: () => controller.addPartToBatch(
                main,
                EffectivePartSelection(
                  partId: part.partId,
                  partCode: part.partCode,
                  isMain: false,
                ),
              ),
            ),
          ),
      ],
    ),
  );
}

class _AddPartChip extends StatelessWidget {
  const _AddPartChip({
    required this.label,
    required this.alreadyAdded,
    required this.onAdd,
    super.key,
  });

  final String label;
  final bool alreadyAdded;
  final bool Function() onAdd;

  @override
  Widget build(BuildContext context) => ActionChip(
    avatar: Icon(
      alreadyAdded ? Icons.check_circle : Icons.add_circle_outline,
      color: alreadyAdded ? const Color(0xFF166534) : null,
    ),
    label: Text(label, overflow: TextOverflow.ellipsis),
    backgroundColor: alreadyAdded ? const Color(0xFFDCFCE7) : null,
    onPressed: alreadyAdded ? null : () => onAdd(),
  );
}

class _BatchReview extends StatelessWidget {
  const _BatchReview({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => ListView(
    key: const Key('batch-review'),
    padding: const EdgeInsets.all(14),
    children: [
      Row(
        children: [
          const Expanded(
            child: Text(
              'Grubu Gözden Geçir',
              style: TextStyle(fontSize: 20, fontWeight: FontWeight.w900),
            ),
          ),
          IconButton(
            key: const Key('batch-exit-review'),
            tooltip: 'Grup Seçimini İptal Et',
            onPressed: controller.exitBatchSelection,
            icon: const Icon(Icons.close),
          ),
        ],
      ),
      const SizedBox(height: 6),
      Text(
        '${controller.batchCart.length} parça yapılandırıldı.',
        key: const Key('batch-review-count'),
      ),
      const SizedBox(height: 14),
      for (final item in controller.batchCart)
        Card(
          key: Key('batch-review-item-${item.effectivePart.partId}'),
          child: ListTile(
            title: Text(item.effectivePart.partCode),
            subtitle: Text(
              '${_containerActionLabel(item.actionCode)} · '
              '${knownOr(item.slotDisplayName)} · '
              'Varyant ${knownOr(item.variantCode)}',
            ),
            trailing: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                IconButton(
                  key: Key('batch-review-edit-${item.effectivePart.partId}'),
                  tooltip: 'Yapılandırmayı Değiştir',
                  onPressed: controller.busy
                      ? null
                      : () => controller.reconfigureBatchItem(item.cartItemId),
                  icon: const Icon(Icons.edit_outlined),
                ),
                IconButton(
                  key: Key('batch-review-remove-${item.effectivePart.partId}'),
                  tooltip: 'Kaldır',
                  onPressed: controller.busy
                      ? null
                      : () => controller.removePartFromBatch(item.cartItemId),
                  icon: const Icon(Icons.delete_outline),
                ),
              ],
            ),
          ),
        ),
      const SizedBox(height: 20),
      ElevatedButton.icon(
        key: const Key('batch-submit'),
        onPressed: controller.busy ? null : controller.submitBatch,
        icon: const Icon(Icons.send),
        label: Text(
          controller.batchCart.length == 1
              ? 'Talebi Gönder'
              : '${controller.batchCart.length} Talebi Gönder',
        ),
      ),
      const SizedBox(height: 40),
    ],
  );
}

String _partDisplay(String code, String? description) {
  final value = description?.trim();
  return value == null || value.isEmpty ? code : '$code · $value';
}

String _containerActionLabel(String? action) => switch (action) {
  'FULL' => 'Kasa Doldu',
  'EMPTY' => 'Kasa Boş',
  _ => 'İşlem seçilmedi',
};
