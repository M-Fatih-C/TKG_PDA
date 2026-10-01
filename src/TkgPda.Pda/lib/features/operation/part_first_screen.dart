import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../data/models/pda_models.dart';
import 'transport_action_panel.dart';
import 'production_session_panel.dart';

/// Final Operator workspace after a machine is selected. Part and compatible
/// variant resolution stay on this page; once proven, the business-operation
/// buttons appear here without a physical-slot or layout step.
class PartFirstSelector extends StatefulWidget {
  const PartFirstSelector({required this.controller, super.key});

  final AppController controller;

  @override
  State<PartFirstSelector> createState() => _PartFirstSelectorState();
}

class _PartFirstSelectorState extends State<PartFirstSelector> {
  static const _searchThreshold = 10;

  final _searchController = TextEditingController();
  String _query = '';

  AppController get controller => widget.controller;

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (controller.partOptionsLoading && controller.partOptions.isEmpty) {
      return const Center(
        key: Key('part-options-loading'),
        child: CircularProgressIndicator(),
      );
    }
    if (controller.partOptionsError case final error?) {
      return Padding(
        padding: const EdgeInsets.all(14),
        child: _RetryState(
          key: const Key('part-options-error'),
          retryKey: const Key('part-options-retry'),
          message: error,
          onRetry: controller.reloadPartOptions,
        ),
      );
    }
    final showSearch = controller.partOptions.length > _searchThreshold;
    final normalizedQuery = _query.trim().toUpperCase();
    final visibleOptions = normalizedQuery.isEmpty
        ? controller.partOptions
        : controller.partOptions
              .where(
                (option) =>
                    option.partCode.toUpperCase().contains(normalizedQuery),
              )
              .toList(growable: false);
    final activeRequests =
        controller.context?.activeTransportRequests ?? const [];
    return RefreshIndicator(
      onRefresh: () async {
        await controller.reloadPartOptions();
        await controller.refreshContext();
      },
      child: ListView(
        key: const Key('part-first-selector'),
        padding: const EdgeInsets.all(16),
        children: [
          if (controller.usesProductionWorkflow) ...[
            ProductionSessionPanel(controller: controller),
            const SizedBox(height: 12),
          ],
          Text(
            controller.selectedMainPart == null
                ? 'Üretilecek Ana Parça'
                : 'Seçilen Ürün ve İlgili Parçalar',
            style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w900),
          ),
          const SizedBox(height: 6),
          Text(
            controller.selectedMainPart == null
                ? 'Üretilecek ana ürünü seçin.'
                : 'Ana ürün sabit kalır. Taşıma talebi için ilgili parçayı seçebilirsiniz.',
          ),
          if (controller.batchMode) ...[
            const SizedBox(height: 12),
            Container(
              key: const Key('batch-mode-banner'),
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
              decoration: BoxDecoration(
                color: Theme.of(context).colorScheme.primaryContainer,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  Icon(
                    Icons.playlist_add_check,
                    size: 20,
                    color: Theme.of(context).colorScheme.onPrimaryContainer,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      'Toplu Taşıma Modu Açık: Parça ve kasa butonuna bastığınızda talep sepete eklenir.',
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w700,
                        color: Theme.of(context).colorScheme.onPrimaryContainer,
                      ),
                    ),
                  ),
                  TextButton(
                    key: const Key('batch-exit'),
                    onPressed: controller.exitBatchSelection,
                    child: const Text('Kapat'),
                  ),
                ],
              ),
            ),
          ],
          if (controller.selectedMainPart == null) ...[
            const SizedBox(height: 16),
            const _SectionLabel('Ana Parçalar'),
            const SizedBox(height: 10),
            if (showSearch) ...[
              TextField(
                key: const Key('main-part-search'),
                controller: _searchController,
                autocorrect: false,
                textInputAction: TextInputAction.search,
                decoration: InputDecoration(
                  labelText: 'Parça kodu ara',
                  prefixIcon: const Icon(Icons.search),
                  suffixIcon: _query.isEmpty
                      ? null
                      : IconButton(
                          key: const Key('clear-main-part-search'),
                          tooltip: 'Aramayı temizle',
                          onPressed: () {
                            _searchController.clear();
                            setState(() => _query = '');
                          },
                          icon: const Icon(Icons.clear),
                        ),
                ),
                onChanged: (value) => setState(() => _query = value),
              ),
              const SizedBox(height: 12),
            ],
            if (controller.partOptions.isEmpty)
              const Text('Bu makine için tanımlı parça bulunamadı.')
            else if (visibleOptions.isEmpty)
              const Text(
                'Bu kodla eşleşen parça bulunamadı.',
                key: Key('main-part-search-empty'),
              )
            else
              _MainPartChips(controller: controller, options: visibleOptions),
          ] else ...[
            const SizedBox(height: 12),
            Align(
              alignment: Alignment.centerLeft,
              child: OutlinedButton.icon(
                key: const Key('change-main-part'),
                onPressed: !controller.canChangePartSelection
                    ? null
                    : () {
                        _searchController.clear();
                        setState(() => _query = '');
                        controller.resetPartSelection();
                      },
                icon: const Icon(Icons.swap_horiz),
                label: const Text('Ana Parçayı Değiştir'),
              ),
            ),
          ],
          if (controller.selectedMainPart case final main?) ...[
            const SizedBox(height: 10),
            _EffectivePartCard(main: main, controller: controller),
          ],
          if (controller.selectedEffectivePart != null) ...[
            const SizedBox(height: 18),
            _CompatibleVariantCard(controller: controller),
          ],
          if (activeRequests.isNotEmpty) ...[
            const SizedBox(height: 18),
            OperatorActiveTransportList(
              requests: activeRequests,
              controller: controller,
            ),
          ],
          const SizedBox(height: 40),
        ],
      ),
    );
  }
}

/// A consistent, visually subordinate section heading used to keep
/// "Ana Parçalar" and "Alternatif / İlgili Parçalar" clearly separated.
class _SectionLabel extends StatelessWidget {
  const _SectionLabel(this.text);

  final String text;

  // Deliberately not upper-cased: Dart's invariant toUpperCase() mangles the
  // Turkish dotted i ("İlgili" -> "İLGILI").
  @override
  Widget build(BuildContext context) => Text(
    text,
    style: const TextStyle(
      fontSize: 13,
      fontWeight: FontWeight.w900,
      letterSpacing: 0.4,
      color: Color(0xFF475569),
    ),
  );
}

class _MainPartChips extends StatelessWidget {
  const _MainPartChips({required this.controller, required this.options});

  final AppController controller;
  final List<MachineMainPartOption> options;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        for (final option in options)
          ConstrainedBox(
            constraints: BoxConstraints(maxWidth: constraints.maxWidth),
            child: _MainPartChip(option: option, controller: controller),
          ),
      ],
    ),
  );
}

/// One click selects the main part as the effective part and starts the
/// compatible-variant lookup — there is no second confirmation chip. The
/// part code leads; an unknown description is simply omitted rather than
/// repeating "Tanımlı değil" on every chip.
class _MainPartChip extends StatelessWidget {
  const _MainPartChip({required this.option, required this.controller});

  final MachineMainPartOption option;
  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final selected = controller.selectedMainPart?.partId == option.partId;
    final description = option.description?.trim();
    return ChoiceChip(
      key: Key('main-part-${option.partId}'),
      selected: selected,
      backgroundColor: Colors.white,
      selectedColor: const Color(0xFFE0F2FE),
      side: BorderSide(
        color: selected ? const Color(0xFF0B4F6C) : const Color(0xFFCBD5E1),
        width: selected ? 2.5 : 1,
      ),
      elevation: selected ? 2 : 0,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      label: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            option.partCode,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: 16,
              fontWeight: selected ? FontWeight.w900 : FontWeight.w700,
              color: selected ? const Color(0xFF0B4F6C) : const Color(0xFF0F172A),
            ),
          ),
          if (description != null && description.isNotEmpty)
            Text(
              description,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: selected ? const Color(0xFF0369A1) : const Color(0xFF475569),
              ),
            ),
        ],
      ),
      onSelected: !controller.canChangePartSelection
          ? null
          : (_) {
              FocusScope.of(context).unfocus();
              controller.selectMainPartOption(option);
            },
    );
  }
}

class _EffectivePartCard extends StatelessWidget {
  const _EffectivePartCard({required this.main, required this.controller});

  final MachineMainPartOption main;
  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final effective = controller.selectedEffectivePart;
    final related = main.relatedParts
        .where((part) => part.relationType == 'RELATED')
        .toList(growable: false);
    final alt = main.relatedParts
        .where((part) => part.relationType == 'ALT_PART')
        .toList(growable: false);
    final other = main.relatedParts
        .where(
          (part) =>
              part.relationType != 'RELATED' && part.relationType != 'ALT_PART',
        )
        .toList(growable: false);

    final usingMain = effective?.isMain ?? false;
    final hasContextualParts =
        related.isNotEmpty || alt.isNotEmpty || other.isNotEmpty;
    final selectedRelation = main.relatedParts
        .where((part) => part.partId == effective?.partId)
        .map((part) => part.relationType)
        .toSet();
    final requestedPartLabel = usingMain
        ? 'Ana ürün için talep: ${main.partCode}'
        : selectedRelation.length == 1 && selectedRelation.single == 'ALT_PART'
        ? 'Alternatif parça için talep: ${effective?.partCode ?? ''}'
        : selectedRelation.length == 1 && selectedRelation.single == 'RELATED'
        ? 'İlgili parça için talep: ${effective?.partCode ?? ''}'
        : 'Seçilen parça için talep: ${effective?.partCode ?? ''}';

    return Card(
      key: Key('main-part-detail-${main.partId}'),
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: const BorderSide(color: Color(0xFFCBD5E1)),
      ),
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(6),
                  decoration: BoxDecoration(
                    color: const Color(0xFF0B4F6C).withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: const Icon(
                    Icons.inventory_2_outlined,
                    size: 20,
                    color: Color(0xFF0B4F6C),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text(
                        'SEÇİLİ ANA PARÇA',
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w800,
                          letterSpacing: 0.8,
                          color: Color(0xFF64748B),
                        ),
                      ),
                      Text(
                        main.partCode,
                        style: const TextStyle(
                          fontSize: 17,
                          fontWeight: FontWeight.w900,
                          color: Color(0xFF0F172A),
                        ),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              decoration: BoxDecoration(
                color: usingMain
                    ? const Color(0xFFF1F5F9)
                    : const Color(0xFFEFF6FF),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: usingMain
                      ? const Color(0xFFCBD5E1)
                      : const Color(0xFF93C5FD),
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    usingMain ? Icons.check_circle_outline : Icons.alt_route,
                    size: 16,
                    color: usingMain ? const Color(0xFF0B4F6C) : const Color(0xFF1D4ED8),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      requestedPartLabel,
                      key: const Key('effective-part-summary'),
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w700,
                        color: usingMain ? const Color(0xFF334155) : const Color(0xFF1E40AF),
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
            ),
            if (!usingMain) ...[
              const SizedBox(height: 4),
              TextButton.icon(
                key: const Key('request-main-part'),
                onPressed: !controller.canChangeEffectivePart
                    ? null
                    : () => controller.selectEffectivePart(
                        EffectivePartSelection(
                          partId: main.partId,
                          partCode: main.partCode,
                          isMain: true,
                        ),
                      ),
                icon: const Icon(Icons.arrow_back, size: 16),
                label: const Text('Ana Ürün İçin Talep Oluştur'),
              ),
            ],
            if (hasContextualParts) ...[
              const Divider(height: 18),
              const _SectionLabel('Alternatif / İlgili Parçalar'),
              const SizedBox(height: 2),
              const Text(
                'Taşıma talebi için ilgili parçayı seçebilirsiniz.',
                style: TextStyle(fontSize: 12, color: Color(0xFF475569)),
              ),
            ],
            if (related.isNotEmpty) ...[
              const SizedBox(height: 14),
              const Text(
                'İlgili Parçalar',
                style: TextStyle(
                  fontWeight: FontWeight.w800,
                  color: Color(0xFF0369A1),
                ),
              ),
              const SizedBox(height: 8),
              _RelatedPartChips(
                parts: related,
                keyPrefix: 'related',
                effective: effective,
                controller: controller,
                isAlternative: false,
              ),
            ],
            if (alt.isNotEmpty) ...[
              const SizedBox(height: 14),
              const Text(
                'Alternatif Parçalar',
                style: TextStyle(
                  fontWeight: FontWeight.w800,
                  color: Color(0xFFB45309),
                ),
              ),
              const SizedBox(height: 8),
              _RelatedPartChips(
                parts: alt,
                keyPrefix: 'alt',
                effective: effective,
                controller: controller,
                isAlternative: true,
              ),
            ],
            if (other.isNotEmpty) ...[
              const SizedBox(height: 16),
              const Text(
                'Diğer Parçalar',
                style: TextStyle(fontWeight: FontWeight.w800, fontSize: 14),
              ),
              const SizedBox(height: 8),
              _RelatedPartChips(
                parts: other,
                keyPrefix: 'other',
                effective: effective,
                controller: controller,
                isAlternative: false,
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _RelatedPartChips extends StatelessWidget {
  const _RelatedPartChips({
    required this.parts,
    required this.keyPrefix,
    required this.effective,
    required this.controller,
    required this.isAlternative,
  });

  final List<MachineRelatedPart> parts;
  final String keyPrefix;
  final EffectivePartSelection? effective;
  final AppController controller;
  final bool isAlternative;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        for (final part in parts)
          ConstrainedBox(
            constraints: BoxConstraints(maxWidth: constraints.maxWidth),
            child: _RelatedPartChip(
              chipKey: Key('$keyPrefix-part-${part.partId}'),
              part: part,
              selected:
                  effective != null &&
                  !effective!.isMain &&
                  effective!.partId == part.partId,
              controller: controller,
              isAlternative: isAlternative,
            ),
          ),
      ],
    ),
  );
}

class _RelatedPartChip extends StatelessWidget {
  const _RelatedPartChip({
    required this.chipKey,
    required this.part,
    required this.selected,
    required this.controller,
    required this.isAlternative,
  });

  final Key chipKey;
  final MachineRelatedPart part;
  final bool selected;
  final AppController controller;
  final bool isAlternative;

  @override
  Widget build(BuildContext context) {
    final selectedBg = isAlternative
        ? const Color(0xFFFEF3C7)
        : const Color(0xFFE0F2FE);
    final selectedBorder = isAlternative
        ? const Color(0xFFD97706)
        : const Color(0xFF0284C7);
    final selectedText = isAlternative
        ? const Color(0xFF92400E)
        : const Color(0xFF0369A1);

    return ChoiceChip(
      key: chipKey,
      selected: selected,
      backgroundColor: Colors.white,
      selectedColor: selectedBg,
      side: BorderSide(
        color: selected ? selectedBorder : const Color(0xFFCBD5E1),
        width: selected ? 2.5 : 1,
      ),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
      label: Text(
        part.partCode,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontSize: 15,
          fontWeight: selected ? FontWeight.w900 : FontWeight.w700,
          color: selected ? selectedText : const Color(0xFF1E293B),
        ),
      ),
      onSelected: !controller.canChangeEffectivePart
          ? null
          : (_) {
              FocusScope.of(context).unfocus();
              controller.selectEffectivePart(
                EffectivePartSelection(
                  partId: part.partId,
                  partCode: part.partCode,
                  isMain: false,
                ),
              );
            },
    );
  }
}

class _CompatibleVariantCard extends StatelessWidget {
  const _CompatibleVariantCard({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) {
    if (controller.compatibleVariantsLoading) {
      return const Card(
        key: Key('compatible-variants-loading'),
        child: Padding(
          padding: EdgeInsets.all(20),
          child: Center(child: CircularProgressIndicator()),
        ),
      );
    }
    if (controller.compatibleVariantsError case final error?) {
      return _RetryState(
        key: const Key('compatible-variants-error'),
        retryKey: const Key('compatible-variants-retry'),
        message: error,
        onRetry: controller.retryCompatibleVariants,
      );
    }
    final variants = controller.compatibleVariants;
    if (variants == null) {
      return const SizedBox.shrink();
    }
    if (variants.isEmpty) {
      final batchVariantCode = controller.batchTargetVariantCode;
      return Card(
        key: const Key('zero-compatible-variants'),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Text(
            batchVariantCode == null
                ? 'Uyumlu varyant bulunamadı.'
                : 'Bu parça, grubun $batchVariantCode varyantıyla uyumlu değil.',
            key: const Key('zero-variants-message'),
            style: const TextStyle(fontWeight: FontWeight.w700),
          ),
        ),
      );
    }
    // A single compatible variant resolves internally while RELATED/ALT
    // alternatives remain reachable on the same page. It is informational,
    // not a one-item selectable list.
    if (variants.length == 1) {
      final variant = variants.single;
      return Card(
        key: const Key('single-compatible-variant'),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              const Icon(Icons.check_circle, color: Color(0xFF15803D)),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  '${variant.variantCode} · ${variant.variantName} (otomatik seçildi)',
                  key: const Key('single-compatible-variant-text'),
                  style: const TextStyle(fontWeight: FontWeight.w700),
                ),
              ),
            ],
          ),
        ),
      );
    }
    return Card(
      key: const Key('multiple-compatible-variants'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Padding(
            padding: EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: Text(
              'Uyumlu Varyantlar',
              style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
            ),
          ),
          for (final variant in variants)
            ListTile(
              key: Key('compatible-variant-${variant.variantId}'),
              title: Text('${variant.variantCode} · ${variant.variantName}'),
              trailing:
                  controller.resolvedCompatibleVariant?.variantId ==
                      variant.variantId
                  ? const Icon(Icons.check_circle, color: Color(0xFF15803D))
                  : null,
              onTap: controller.busy
                  ? null
                  : () {
                      FocusScope.of(context).unfocus();
                      controller.selectCompatibleVariant(variant);
                    },
            ),
          const SizedBox(height: 8),
        ],
      ),
    );
  }
}

class _RetryState extends StatelessWidget {
  const _RetryState({
    required this.message,
    required this.onRetry,
    required this.retryKey,
    super.key,
  });

  final String message;
  final Future<void> Function() onRetry;
  final Key retryKey;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(message),
          const SizedBox(height: 10),
          OutlinedButton.icon(
            key: retryKey,
            onPressed: () => onRetry(),
            icon: const Icon(Icons.refresh),
            label: const Text('Yeniden Dene'),
          ),
        ],
      ),
    ),
  );
}
