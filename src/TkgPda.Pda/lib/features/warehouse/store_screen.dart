import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../app/app_controller.dart' show AppStage;
import '../../app/design_tokens.dart';
import '../../data/models/preparation_area.dart';
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
    this.onLogout,
    this.operatorLabel,
    super.key,
  });

  final StoreCoordinator coordinator;
  final VoidCallback onBack;
  final void Function(AppStage stage)? onNavigate;
  final VoidCallback? onLogout;
  final String? operatorLabel;

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
            if (operatorLabel case final label?)
              Text(
                label,
                key: const Key('operator-user-banner'),
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.normal,
                ),
                overflow: TextOverflow.ellipsis,
              ),
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
          if (onLogout != null)
            IconButton(
              key: const Key('logout-button'),
              tooltip: 'Çıkış',
              onPressed: onLogout,
              icon: const Icon(Icons.logout),
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
    if (coordinator.locations.isEmpty &&
        coordinator.preparationAreas.isEmpty &&
        coordinator.emptyContainerDemands.isEmpty) {
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
        if (constraints.maxWidth >= PdaScale.compactBreakpoint) {
          return Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(child: _StoreAreaList(coordinator: coordinator)),
              SizedBox(
                width: constraints.maxWidth >= 1400 ? 390 : 340,
                child: _FinishedGoodsDemandList(coordinator: coordinator),
              ),
            ],
          );
        }
        return Column(
          children: [
            Expanded(flex: 3, child: _StoreAreaList(coordinator: coordinator)),
            const Divider(height: 1),
            Expanded(
              flex: 2,
              child: _FinishedGoodsDemandList(coordinator: coordinator),
            ),
          ],
        );
      },
    );
  }
}

class _StoreAreaList extends StatelessWidget {
  const _StoreAreaList({required this.coordinator});

  final StoreCoordinator coordinator;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final gutter = PdaScale.gutter(context);
      final deliveryColumns = (constraints.maxWidth / 330).floor().clamp(1, 2);
      final preparationColumns = (constraints.maxWidth / 300).floor().clamp(
        1,
        3,
      );
      return CustomScrollView(
        key: const Key('store-location-list'),
        slivers: [
          SliverPadding(
            padding: EdgeInsets.fromLTRB(gutter, gutter, gutter, gutter / 2),
            sliver: const SliverToBoxAdapter(
              child: _SectionTitle(
                title: 'Mamul Teslim Alanları',
                subtitle:
                    'Robotun bitmiş ürün kasalarını bıraktığı 2 sabit alan',
                icon: Icons.inventory_2_outlined,
              ),
            ),
          ),
          SliverPadding(
            padding: EdgeInsets.symmetric(horizontal: gutter),
            sliver: SliverGrid.builder(
              gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
                crossAxisCount: deliveryColumns,
                crossAxisSpacing: gutter,
                mainAxisSpacing: gutter,
                mainAxisExtent: 270,
              ),
              itemCount: coordinator.locations.length,
              itemBuilder: (context, index) => _LocationCard(
                coordinator: coordinator,
                location: coordinator.locations[index],
              ),
            ),
          ),
          SliverPadding(
            padding: EdgeInsets.fromLTRB(
              gutter,
              gutter * 1.5,
              gutter,
              gutter / 2,
            ),
            sliver: SliverToBoxAdapter(
              child: _SectionTitle(
                title: 'Boş Kasa Hazırlık Alanları',
                subtitle:
                    '${coordinator.emptyContainerDemands.length} bekleyen talep · Her alanda en fazla 1 palet',
                icon: Icons.inbox_outlined,
              ),
            ),
          ),
          SliverPadding(
            padding: EdgeInsets.fromLTRB(gutter, 0, gutter, gutter),
            sliver: SliverGrid.builder(
              gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
                crossAxisCount: preparationColumns,
                crossAxisSpacing: gutter,
                mainAxisSpacing: gutter,
                mainAxisExtent: 285,
              ),
              itemCount: coordinator.preparationAreas.length,
              itemBuilder: (context, index) => _EmptyPreparationCard(
                coordinator: coordinator,
                area: coordinator.preparationAreas[index],
              ),
            ),
          ),
        ],
      );
    },
  );
}

class _FinishedGoodsDemandList extends StatelessWidget {
  const _FinishedGoodsDemandList({required this.coordinator});

  final StoreCoordinator coordinator;

  @override
  Widget build(BuildContext context) {
    final demands = coordinator.emptyContainerDemands;
    return Container(
      key: const Key('finished-goods-demand-panel'),
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surfaceContainerLow,
        border: const Border(left: BorderSide(color: Color(0xFFE2E8F0))),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: EdgeInsets.all(PdaScale.gutter(context)),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Gelen Talepler',
                        style: TextStyle(
                          fontSize: PdaScale.title(context),
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      Text(
                        '${demands.length} bekleyen boş kasa talebi',
                        style: TextStyle(
                          fontSize: PdaScale.caption(context),
                          color: Theme.of(context).colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
                if (coordinator.hasUnseenDemands)
                  TextButton(
                    key: const Key('acknowledge-finished-goods-demands'),
                    onPressed: coordinator.acknowledgeDemands,
                    child: Text('${coordinator.unseenDemandIds.length} yeni'),
                  ),
              ],
            ),
          ),
          Expanded(
            child: demands.isEmpty
                ? const PdaEmptyState(
                    icon: Icons.checklist_rtl_rounded,
                    title: 'Bekleyen talep yok.',
                    subtitle:
                        'Makine boş kasa talepleri geldiğinde burada listelenecektir.',
                    textKey: Key('finished-goods-demand-list-empty'),
                  )
                : ListView.separated(
                    key: const Key('finished-goods-demand-list'),
                    itemCount: demands.length,
                    separatorBuilder: (_, _) => const Divider(height: 1),
                    itemBuilder: (context, index) =>
                        _FinishedGoodsDemandTile(demand: demands[index]),
                  ),
          ),
        ],
      ),
    );
  }
}

class _FinishedGoodsDemandTile extends StatelessWidget {
  const _FinishedGoodsDemandTile({required this.demand});

  final WarehouseDemand demand;

  @override
  Widget build(BuildContext context) {
    final variant = demand.variantName ?? demand.variantCode;
    return Container(
      margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: const Color(0xFFE2E8F0)),
      ),
      child: ListTile(
        key: Key('finished-goods-demand-${demand.requestNo}'),
        dense: true,
        title: Text(
          demand.partCode ?? 'Parça bilinmiyor',
          style: TextStyle(
            fontSize: PdaScale.body(context),
            fontWeight: FontWeight.w800,
            color: const Color(0xFF0F172A),
          ),
          overflow: TextOverflow.ellipsis,
        ),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 2),
            Text(
              '${demand.machineCode} · Talep #${demand.requestNo}',
              style: TextStyle(
                fontSize: PdaScale.caption(context),
                fontWeight: FontWeight.w600,
                color: const Color(0xFF475569),
              ),
            ),
            if (variant != null && variant.isNotEmpty)
              Text(
                'Varyant: $variant',
                style: TextStyle(fontSize: PdaScale.caption(context)),
              ),
            Text(
              'Palet/Kasa: ${_demandContainerLabel(demand)}',
              style: TextStyle(fontSize: PdaScale.caption(context)),
            ),
            Text(
              _demandWaitedLabel(demand.requestedAt),
              style: const TextStyle(fontSize: 11, color: Color(0xFF64748B)),
            ),
            if (demand.claimedByAreaNumber case final number?)
              Text(
                'Hazırlık Alanı $number hazırlıyor',
                style: TextStyle(
                  color: const Color(0xFF2F6FED),
                  fontWeight: FontWeight.w700,
                  fontSize: PdaScale.caption(context),
                ),
              ),
          ],
        ),
        trailing: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
          decoration: BoxDecoration(
            color: const Color(0xFF0B4F6C).withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(6),
          ),
          child: const Text(
            'Boş Kasa',
            style: TextStyle(
              fontWeight: FontWeight.w700,
              color: Color(0xFF0B4F6C),
            ),
          ),
        ),
      ),
    );
  }
}

String _demandWaitedLabel(DateTime? requestedAt) {
  if (requestedAt == null) return 'İstenme zamanı bilinmiyor';
  final waited = DateTime.now().difference(requestedAt);
  final clock = DateFormat('HH:mm').format(requestedAt);
  if (waited.isNegative) return 'İstendi: $clock';
  if (waited.inMinutes < 1) return 'İstendi: $clock · az önce';
  if (waited.inMinutes < 60) {
    return 'İstendi: $clock · ${waited.inMinutes} dk';
  }
  return 'İstendi: $clock · ${waited.inHours} sa ${waited.inMinutes % 60} dk';
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle({
    required this.title,
    required this.subtitle,
    required this.icon,
  });
  final String title;
  final String subtitle;
  final IconData icon;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      Icon(icon, color: Theme.of(context).colorScheme.primary),
      const SizedBox(width: 10),
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w800),
            ),
            Text(
              subtitle,
              style: TextStyle(
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ),
    ],
  );
}

class _EmptyPreparationCard extends StatelessWidget {
  const _EmptyPreparationCard({required this.coordinator, required this.area});
  final StoreCoordinator coordinator;
  final PreparationArea area;

  @override
  Widget build(BuildContext context) {
    final pallet = area.pallet;
    final busy =
        coordinator.isBusy(area.id) ||
        (pallet != null && coordinator.isBusy(pallet.id));
    final demand = pallet?.transportRequestId == null
        ? null
        : coordinator.emptyContainerDemands
              .where((item) => item.requestId == pallet!.transportRequestId)
              .firstOrNull;
    return Card(
      key: Key('empty-preparation-${area.areaNumber}'),
      child: Padding(
        padding: EdgeInsets.all(PdaScale.cardPadding(context)),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              area.displayName,
              style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17),
            ),
            const SizedBox(height: 10),
            if (pallet == null) ...[
              const Text(
                'ALAN BOŞ',
                style: TextStyle(
                  fontWeight: FontWeight.w800,
                  color: Color(0xFF475569),
                ),
              ),
              const SizedBox(height: 6),
              const Text(
                'Bekleyen bir talebi seçip istenen boş paleti bu alana yerleştirin.',
              ),
            ] else ...[
              Text(
                pallet.isReady
                    ? 'HAZIR · ROBOT BEKLİYOR'
                    : 'HAZIRLIK YAPILIYOR',
                style: TextStyle(
                  fontWeight: FontWeight.w800,
                  color: pallet.isReady
                      ? const Color(0xFF166534)
                      : const Color(0xFFB45309),
                ),
              ),
              const SizedBox(height: 6),
              Text(
                'Makine: ${demand?.machineCode ?? pallet.targetMachineCode ?? '—'}',
              ),
              Text(
                'Varyant: ${demand?.variantName ?? demand?.variantCode ?? '—'}',
              ),
              Text('Parça: ${demand?.partCode ?? '—'}'),
              Text(
                'Palet/Kasa: ${demand == null ? (pallet.containerCode.isEmpty ? '—' : pallet.containerCode) : _demandContainerLabel(demand)}',
              ),
              if (pallet.requestNo != null) Text('Talep #${pallet.requestNo}'),
            ],
            const Spacer(),
            if (busy)
              const LinearProgressIndicator()
            else if (pallet == null)
              SizedBox(
                width: double.infinity,
                height: 48,
                child: FilledButton.icon(
                  key: Key('select-empty-demand-${area.areaNumber}'),
                  onPressed:
                      coordinator.inFlight ||
                          coordinator.unclaimedEmptyContainerDemands.isEmpty
                      ? null
                      : () => _selectDemand(context),
                  icon: const Icon(Icons.playlist_add_check),
                  label: Text(
                    coordinator.unclaimedEmptyContainerDemands.isEmpty
                        ? 'Bekleyen talep yok'
                        : 'Talep Seç',
                  ),
                ),
              )
            else if (!pallet.isReady)
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      onPressed: coordinator.inFlight
                          ? null
                          : () => coordinator.releasePreparation(
                              pallet,
                              'WITHDRAWN',
                            ),
                      child: const Text('Geri Al'),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: FilledButton(
                      key: Key('mark-empty-ready-${area.areaNumber}'),
                      onPressed: coordinator.inFlight
                          ? null
                          : () => coordinator.markPreparationReady(pallet),
                      child: const Text('Hazır'),
                    ),
                  ),
                ],
              )
            else if (pallet.canConfirmCollected)
              SizedBox(
                width: double.infinity,
                height: 48,
                child: FilledButton(
                  key: Key('empty-collected-${area.areaNumber}'),
                  onPressed: coordinator.inFlight
                      ? null
                      : () =>
                            coordinator.releasePreparation(pallet, 'COLLECTED'),
                  child: const Text('Robot Aldı'),
                ),
              )
            else
              const SizedBox(
                width: double.infinity,
                child: Text(
                  'Talep sırada. Robot görevi başlayınca alan kapatılabilir.',
                  textAlign: TextAlign.center,
                ),
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _selectDemand(BuildContext context) async {
    final selected = await showModalBottomSheet<WarehouseDemand>(
      context: context,
      builder: (sheetContext) => SafeArea(
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.all(16),
          children: [
            const Text(
              'Boş Kasa Talebi Seç',
              style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
            ),
            const SizedBox(height: 8),
            for (final demand in coordinator.unclaimedEmptyContainerDemands)
              ListTile(
                key: Key('empty-demand-${demand.requestId}'),
                title: Text(
                  '${demand.machineCode} · ${demand.partCode ?? 'Parça'}',
                ),
                subtitle: Text(
                  '${demand.variantName ?? demand.variantCode ?? 'Varyant —'} · ${_demandContainerLabel(demand)} · Talep #${demand.requestNo}',
                ),
                enabled:
                    demand.containerTypeId?.isNotEmpty == true ||
                    demand.containerDimensions?.trim().isNotEmpty == true,
                onTap: () => Navigator.pop(sheetContext, demand),
              ),
          ],
        ),
      ),
    );
    if (selected != null && context.mounted) {
      await coordinator.prepareEmptyContainer(area: area, demand: selected);
    }
  }
}

String _demandContainerLabel(WarehouseDemand demand) {
  final code = demand.containerCode?.trim();
  if (code != null && code.isNotEmpty) return code;
  final dimensions = demand.containerDimensions?.trim();
  if (dimensions != null && dimensions.isNotEmpty) {
    return 'Ölçü: ${dimensions.replaceAll('/', ' × ')}';
  }
  return 'Palet/kasa bilgisi yok';
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
              else if (location.isReserved)
                SizedBox(
                  height: 48,
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    onPressed: null,
                    icon: const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                    label: Text(
                      location.reservationType == 'PICKUP'
                          ? 'Robot Kasa Alıyor...'
                          : 'Robot Getiriyor...',
                    ),
                  ),
                )
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
                    onPressed: null,
                    child: const Text('Fiziksel sayım gerekli'),
                  ),
                )
              else if (location.isEnabled)
                SizedBox(
                  height: 48,
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    onPressed: null,
                    icon: const Icon(Icons.precision_manufacturing_outlined),
                    label: const Text('Robot teslim alanı'),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
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
    if (location.isReserved) {
      if (location.reservationType == 'PICKUP') {
        return const _StorePresentation(
          'ROBOT KASA ALIYOR',
          'Robot bu alandaki boş kasayı almaya geliyor.',
          Icons.precision_manufacturing,
          Color(0xFFB45309),
          Color(0xFFFFFBEB),
        );
      }
      return const _StorePresentation(
        'ROBOT MAMUL GETİRİYOR',
        'Robot bu alana dolu mamul kasası bırakmaya geliyor.',
        Icons.precision_manufacturing,
        Color(0xFF0369A1),
        Color(0xFFF0F9FF),
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
      'VERİ UYUŞMAZLIĞI',
      '${parts.join(' · ')} (Tek palet kuralı aşıldı)',
      Icons.warning_amber_rounded,
      const Color(0xFFB91C1C),
      const Color(0xFFFEF2F2),
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
                  unit.loadState == 'FULL'
                      ? (unit.partCode ?? unit.loadPresentation)
                      : unit.loadPresentation,
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
      unit.loadPresentation,
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
