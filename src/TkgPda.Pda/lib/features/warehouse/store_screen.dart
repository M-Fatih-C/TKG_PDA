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
              child: Text(
                '${coordinator.unseenUnitIds.length} yeni',
                style: const TextStyle(color: Colors.white),
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

    return ListView.separated(
      key: const Key('store-location-list'),
      padding: EdgeInsets.all(PdaScale.gutter(context)),
      itemCount: coordinator.locations.length,
      separatorBuilder: (_, _) => SizedBox(height: PdaScale.gutter(context)),
      itemBuilder: (context, index) => _LocationCard(
        coordinator: coordinator,
        location: coordinator.locations[index],
      ),
    );
  }
}

class _LocationCard extends StatelessWidget {
  const _LocationCard({required this.coordinator, required this.location});

  final StoreCoordinator coordinator;
  final StoreLocation location;

  @override
  Widget build(BuildContext context) {
    final state = location.isFull
        ? 'ERROR'
        : location.isEmpty
        ? 'VACANT'
        : 'RUNNING';
    final busy = coordinator.isBusy(location.id);

    return Material(
      key: Key('store-location-${location.code}'),
      color: PdaStatusColors.surfaceOf(state),
      borderRadius: BorderRadius.circular(PdaScale.actionRadius),
      child: Padding(
        padding: EdgeInsets.all(PdaScale.cardPadding(context)),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    '${location.code} · ${location.displayName.isNotEmpty ? location.displayName : location.code}',
                    style: TextStyle(
                      fontSize: PdaScale.title(context),
                      fontWeight: FontWeight.w700,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                _OccupancyChip(location: location),
              ],
            ),
            SizedBox(height: PdaScale.gutter(context) / 2),
            if (location.isEmpty) ...[
              Text(
                'Bu alan boş. Boş kasa varlığı ayrıca kaydedilir.',
                style: TextStyle(fontSize: PdaScale.body(context)),
              ),
              SizedBox(height: PdaScale.gutter(context) / 2),
              SizedBox(
                height: PdaScale.secondaryActionHeight(context),
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
            ]
            else
              for (final unit in location.units)
                _UnitRow(
                  coordinator: coordinator,
                  unit: unit,
                  isNew: coordinator.unseenUnitIds.contains(unit.id),
                ),
            if (busy) ...[
              SizedBox(height: PdaScale.gutter(context) / 2),
              const LinearProgressIndicator(minHeight: 3),
            ] else if (!location.isEmpty) ...[
              SizedBox(height: PdaScale.gutter(context) / 2),
              SizedBox(
                height: PdaScale.secondaryActionHeight(context),
                width: double.infinity,
                child: OutlinedButton.icon(
                  key: Key('clear-location-${location.code}'),
                  // Everything went at once. Individual removal stays available
                  // on each row for the ordinary case.
                  onPressed: coordinator.inFlight
                      ? null
                      : () => coordinator.clearLocation(location),
                  icon: const Icon(Icons.done_all),
                  label: Text('Tümünü aldım (${location.unitCount})'),
                ),
              ),
            ],
          ],
        ),
      ),
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
                Text(_subtitle(unit), style: caption),
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

  static String _subtitle(StoreUnit unit) {
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

class _OccupancyChip extends StatelessWidget {
  const _OccupancyChip({required this.location});

  final StoreLocation location;

  @override
  Widget build(BuildContext context) {
    // Without a stated capacity the chip shows a bare count: a ratio would need
    // a denominator nobody has given.
    final label = location.hasKnownCapacity
        ? '${location.unitCount} / ${location.capacity}'
        : '${location.unitCount} kasa';
    final color = location.isFull
        ? PdaStatusColors.failed
        : location.isNearlyFull
        ? PdaStatusColors.waiting
        : location.isEmpty
        ? PdaStatusColors.unknown
        : PdaStatusColors.running;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: color,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: Colors.white,
          fontSize: PdaScale.caption(context),
          fontWeight: FontWeight.w600,
        ),
      ),
    );
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
