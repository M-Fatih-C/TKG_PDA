import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../app/app_controller.dart' show AppStage;
import '../../app/design_tokens.dart';
import '../../data/models/store_location.dart';
import '../../shared/widgets/pda_empty_state.dart';
import 'store_coordinator.dart';
import 'widgets/warehouse_notification_widgets.dart';

/// The finished-goods half of the warehouse tablet: what the robot left, and
/// the operator's statement that they took it away.
///
/// There is no action that adds a container here. One appears when the transport
/// that carried it completes, so the screen only ever reports and removes.
class StoreScreen extends StatelessWidget {
  const StoreScreen({
    required this.coordinator,
    required this.onBack,
    this.onNavigate,
    super.key,
  });

  final StoreCoordinator coordinator;
  final VoidCallback onBack;
  final void Function(AppStage stage)? onNavigate;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: coordinator,
    builder: (context, _) => Scaffold(
      appBar: AppBar(
        leading: IconButton(
          key: const Key('leave-store'),
          tooltip: 'Geri',
          onPressed: coordinator.inFlight ? null : onBack,
          icon: const Icon(Icons.arrow_back),
        ),
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('Bitmiş Ürün Deposu'),
            if (coordinator.lastSuccessfulRefresh case final last?)
              Text(
                coordinator.error != null
                    ? 'Bağlantı kesildi · Son güncelleme: ${DateFormat('HH:mm:ss').format(last)}'
                    : 'Son güncelleme: ${DateFormat('HH:mm:ss').format(last)}',
                key: coordinator.error != null
                    ? const Key('store-stale-warning')
                    : const Key('store-last-update'),
                style: TextStyle(
                  fontSize: 11,
                  fontWeight: FontWeight.normal,
                  color: coordinator.error != null
                      ? const Color(0xFFFCA5A5)
                      : Colors.white70,
                ),
              ),
          ],
        ),
        actions: [
          if (coordinator.notificationCoordinator case final notifCoord?)
            WarehouseNotificationBell(
              coordinator: notifCoord,
              onNavigate: (stage) {
                if (stage != AppStage.finishedGoodsStore) {
                  onNavigate?.call(stage);
                }
              },
            ),
          if (coordinator.hasUnseenUnits)
            TextButton(
              key: const Key('acknowledge-arrivals'),
              onPressed: coordinator.acknowledgeArrivals,
              child: const Text(
                'Gördüm',
                style: TextStyle(color: Colors.white),
              ),
            ),
          IconButton(
            key: const Key('open-global-tasks'),
            tooltip: 'Görevler',
            onPressed: coordinator.loading
                ? null
                : () => onNavigate?.call(AppStage.tasks),
            icon: const Icon(Icons.assignment_outlined),
          ),
          IconButton(
            key: const Key('refresh-store'),
            tooltip: 'Yenile',
            onPressed: coordinator.loading ? null : () => coordinator.refresh(),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            if (coordinator.notificationCoordinator case final notifCoord?)
              WarehouseNotificationBanner(
                coordinator: notifCoord,
                onNavigate: (stage) {
                  if (stage != AppStage.finishedGoodsStore) {
                    onNavigate?.call(stage);
                  }
                },
              ),
            if (coordinator.error case final message?)
              _Banner(
                key: const Key('store-error'),
                color: PdaStatusColors.failed,
                icon: Icons.error_outline,
                message: message,
              ),
            if (coordinator.fullLocations.isNotEmpty)
              _Banner(
                key: const Key('store-full-warning'),
                color: PdaStatusColors.failed,
                icon: Icons.warehouse_outlined,
                message:
                    '${_names(coordinator.fullLocations)} doldu. Lütfen boşaltınız.',
              )
            else if (coordinator.nearlyFullLocations.isNotEmpty)
              _Banner(
                key: const Key('store-nearly-full-warning'),
                color: PdaStatusColors.waiting,
                icon: Icons.warehouse_outlined,
                message:
                    '${_names(coordinator.nearlyFullLocations)} dolmak üzere. Lütfen boşaltınız.',
              ),
            Expanded(child: _LocationList(coordinator: coordinator)),
          ],
        ),
      ),
    ),
  );

  static String _names(List<StoreLocation> locations) =>
      locations.map((location) => location.displayName).join(', ');
}

class _LocationList extends StatelessWidget {
  const _LocationList({required this.coordinator});

  final StoreCoordinator coordinator;

  @override
  Widget build(BuildContext context) {
    if (coordinator.locations.isEmpty) {
      return Center(
        key: const Key('store-empty'),
        child: Padding(
          padding: EdgeInsets.all(PdaScale.gutter(context) * 2),
          child: PdaEmptyState(
            icon: Icons.warehouse_outlined,
            title: coordinator.loading
                ? 'Depo alanları yükleniyor…'
                : 'Tanımlı depo alanı yok',
            subtitle: coordinator.loading
                ? null
                : 'Tanımlı depo alanı yok. Alanlar müşteri tarafından belirlendikten sonra burada görünecek.',
          ),
        ),
      );
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        final columns = (constraints.maxWidth / 400).floor().clamp(1, 4);
        return GridView.builder(
          key: const Key('store-location-list'),
          padding: EdgeInsets.all(PdaScale.gutter(context)),
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
            crossAxisCount: columns,
            crossAxisSpacing: PdaScale.gutter(context),
            mainAxisSpacing: PdaScale.gutter(context),
            mainAxisExtent: 280,
          ),
          itemCount: coordinator.locations.length,
          itemBuilder: (context, index) => _LocationCard(
            coordinator: coordinator,
            location: coordinator.locations[index],
          ),
        );
      },
    );
  }
}

class _LocationCard extends StatelessWidget {
  const _LocationCard({required this.coordinator, required this.location});

  final StoreCoordinator coordinator;
  final StoreLocation location;

  @override
  Widget build(BuildContext context) {
    final presentation = _StorePresentation.of(location);
    final busy = coordinator.isBusy(location.id);
    final single = location.units.length == 1 ? location.units.single : null;

    return Material(
      key: Key('store-location-${location.code}'),
      color: presentation.background,
      borderRadius: BorderRadius.circular(PdaScale.actionRadius),
      child: InkWell(
        onTap: location.units.isEmpty
            ? null
            : () => _showUnits(context, coordinator, location),
        child: Padding(
          padding: EdgeInsets.all(PdaScale.cardPadding(context)),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                location.displayName.isNotEmpty
                    ? location.displayName
                    : location.code,
                style: TextStyle(
                  fontSize: PdaScale.body(context),
                  fontWeight: FontWeight.w800,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(height: 6),
              Row(
                children: [
                  Icon(
                    presentation.icon,
                    size: 18,
                    color: presentation.foreground,
                  ),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      presentation.label,
                      key: Key('store-state-${location.code}'),
                      style: TextStyle(
                        fontSize: PdaScale.caption(context),
                        fontWeight: FontWeight.w800,
                        color: presentation.foreground,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              Text(
                presentation.summary,
                style: TextStyle(fontSize: PdaScale.caption(context)),
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
              if (location.units.length == 1)
                Text(
                  _UnitRow.subtitle(location.units.single),
                  style: TextStyle(fontSize: PdaScale.caption(context)),
                  maxLines: 3,
                  overflow: TextOverflow.ellipsis,
                )
              else if (location.units.isNotEmpty)
                Text(
                  location.hasKnownCapacity
                      ? '${location.unitCount} / ${location.capacity}'
                      : '${location.unitCount} kasa',
                  style: TextStyle(
                    fontSize: PdaScale.caption(context),
                    fontWeight: FontWeight.w700,
                  ),
                ),
              const Spacer(),
              if (busy)
                const LinearProgressIndicator(minHeight: 3)
              else if (single != null)
                SizedBox(
                  height: 48,
                  width: double.infinity,
                  child: FilledButton(
                    key: Key('remove-unit-${single.id}'),
                    onPressed: coordinator.inFlight
                        ? null
                        : () => coordinator.removeUnit(single),
                    child: const Text('Aldım'),
                  ),
                )
              else if (location.units.length > 1)
                SizedBox(
                  height: 48,
                  width: double.infinity,
                  child: OutlinedButton(
                    key: Key('clear-location-${location.code}'),
                    onPressed: coordinator.inFlight
                        ? null
                        : () => _confirmClear(context, coordinator, location),
                    child: Text('Tümünü Aldım (${location.unitCount})'),
                  ),
                )
              else if (location.isEnabled)
                SizedBox(
                  height: 48,
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    key: Key('record-empty-${location.code}'),
                    onPressed: coordinator.inFlight
                        ? null
                        : () => coordinator.recordEmptyContainer(location),
                    icon: const Icon(Icons.inbox_outlined),
                    label: const Text('Boş kasa bırak'),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _confirmClear(
    BuildContext context,
    StoreCoordinator coordinator,
    StoreLocation location,
  ) async {
    final accepted = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Tümünü al'),
        content: Text(
          '${location.unitCount} kayıt bu alandan alınmış sayılsın mı?',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            key: const Key('confirm-clear-location'),
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Tümünü Aldım'),
          ),
        ],
      ),
    );
    if (accepted == true) await coordinator.clearLocation(location);
  }

  void _showUnits(
    BuildContext context,
    StoreCoordinator coordinator,
    StoreLocation location,
  ) {
    showModalBottomSheet<void>(
      context: context,
      builder: (sheetContext) => SafeArea(
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              location.displayName.isNotEmpty
                  ? location.displayName
                  : location.code,
              style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18),
            ),
            for (final unit in location.units)
              _UnitRow(
                coordinator: coordinator,
                unit: unit,
                isNew: coordinator.unseenUnitIds.contains(unit.id),
              ),
          ],
        ),
      ),
    );
  }
}

class _StorePresentation {
  const _StorePresentation(
    this.label,
    this.summary,
    this.icon,
    this.foreground,
    this.background,
  );

  final String label;
  final String summary;
  final IconData icon;
  final Color foreground;
  final Color background;

  static _StorePresentation of(StoreLocation location) {
    if (!location.isEnabled) {
      return const _StorePresentation(
        'DEVRE DIŞI',
        'Yeni kayıt alınmaz.',
        Icons.block,
        Color(0xFF64748B),
        Color(0xFFF1F5F9),
      );
    }
    if (location.units.isEmpty) {
      return const _StorePresentation(
        'ALAN BOŞ',
        'Bu alan boş. Boş kasa ayrıca kaydedilir.',
        Icons.crop_square,
        Color(0xFF475569),
        Color(0xFFF8FAFC),
      );
    }
    final full = location.units
        .where((unit) => unit.loadState == 'FULL')
        .length;
    final empty = location.units
        .where((unit) => unit.loadState == 'EMPTY')
        .length;
    final unknown = location.units.length - full - empty;
    if (location.units.length == 1) {
      final unit = location.units.single;
      if (unit.loadState == 'FULL') {
        return _StorePresentation(
          'DOLU MAMUL',
          unit.partCode ?? 'Parça bilinmiyor',
          Icons.inventory_2,
          const Color(0xFF166534),
          const Color(0xFFF0FDF4),
        );
      }
      if (unit.loadState == 'EMPTY') {
        return const _StorePresentation(
          'BOŞ KASA VAR',
          '1 Boş Kasa',
          Icons.inbox,
          Color(0xFF1D4ED8),
          Color(0xFFEFF6FF),
        );
      }
      return const _StorePresentation(
        'DURUM BİLİNMİYOR',
        'Yük durumu sunucudan gelmedi.',
        Icons.help_outline,
        Color(0xFF92400E),
        Color(0xFFFFFBEB),
      );
    }
    final parts = <String>[
      if (full > 0) '$full Dolu Mamul',
      if (empty > 0) '$empty Boş Kasa',
      if (unknown > 0) '$unknown Durum Bilinmiyor',
    ];
    return _StorePresentation(
      'KARMA',
      parts.join(' · '),
      Icons.layers,
      const Color(0xFF1E293B),
      const Color(0xFFF8FAFC),
    );
  }
}

class _UnitRow extends StatelessWidget {
  const _UnitRow({
    required this.coordinator,
    required this.unit,
    required this.isNew,
  });

  final StoreCoordinator coordinator;
  final StoreUnit unit;
  final bool isNew;

  @override
  Widget build(BuildContext context) {
    final caption = TextStyle(
      fontSize: PdaScale.caption(context),
      color: Theme.of(context).colorScheme.onSurfaceVariant,
    );
    return Padding(
      key: Key('store-unit-${unit.id}'),
      padding: EdgeInsets.symmetric(vertical: PdaScale.gutter(context) / 3),
      child: Row(
        children: [
          if (isNew)
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: Icon(
                Icons.fiber_new,
                size: 20,
                color: PdaStatusColors.running,
              ),
            ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  unit.isEmptyContainer
                      ? 'Boş kasa'
                      : (unit.partCode ?? 'Parça bilinmiyor'),
                  style: TextStyle(
                    fontSize: PdaScale.body(context),
                    fontWeight: FontWeight.w600,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
                Text(_UnitRow.subtitle(unit), style: caption),
              ],
            ),
          ),
          SizedBox(
            height: PdaScale.secondaryActionHeight(context),
            child: FilledButton(
              key: Key('remove-unit-${unit.id}'),
              onPressed: coordinator.inFlight || coordinator.isBusy(unit.id)
                  ? null
                  : () => coordinator.removeUnit(unit),
              child: const Text('Aldım'),
            ),
          ),
        ],
      ),
    );
  }

  static String subtitle(StoreUnit unit) {
    final parts = <String>[
      unit.isEmptyContainer ? 'Boş kasa' : 'Dolu mamul',
      ?unit.machineCode,
      if (unit.containerCode case final container?) 'Kap: $container',
      if (unit.requestNo case final number?) 'Talep #$number',
      _deliveredLabel(unit.deliveredAt),
    ];
    return parts.join(' · ');
  }
}

class _Banner extends StatelessWidget {
  const _Banner({
    required this.color,
    required this.icon,
    required this.message,
    super.key,
  });

  final Color color;
  final IconData icon;
  final String message;

  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    color: color.withValues(alpha: 0.14),
    padding: EdgeInsets.symmetric(
      horizontal: PdaScale.gutter(context),
      vertical: 10,
    ),
    child: Row(
      children: [
        Icon(icon, color: color),
        SizedBox(width: PdaScale.gutter(context) / 2),
        Expanded(
          child: Text(
            message,
            style: TextStyle(fontSize: PdaScale.body(context)),
          ),
        ),
      ],
    ),
  );
}

/// When the server sent no timestamp the screen says so rather than
/// substituting the tablet's own clock.
String _deliveredLabel(DateTime? deliveredAt) {
  if (deliveredAt == null) return 'Geliş zamanı bilinmiyor';
  final waited = DateTime.now().difference(deliveredAt);
  final clock = DateFormat('HH:mm').format(deliveredAt);
  if (waited.isNegative || waited.inMinutes < 1) return 'Geldi: $clock';
  if (waited.inMinutes < 60) return 'Geldi: $clock · ${waited.inMinutes} dk';
  if (waited.inHours < 24) {
    return 'Geldi: $clock · ${waited.inHours} sa ${waited.inMinutes % 60} dk';
  }
  return 'Geldi: ${DateFormat('dd.MM HH:mm').format(deliveredAt)}';
}
