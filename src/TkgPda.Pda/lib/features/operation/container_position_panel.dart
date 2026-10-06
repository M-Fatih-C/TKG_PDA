import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../app/design_tokens.dart';
import '../../data/models/pda_models.dart';

/// WP-33 business-action surface. It consumes only the backend's logical
/// position projection and never receives a physical slot identifier.
///
/// This is the one surface an operator touches all shift, so it gets the space
/// and the type scale to match. It previously lived in a fixed 124px strip with
/// 12px labels and a 9px disabled reason, which is unreadable at arm's length
/// on a line terminal; sizes now come from [PdaScale].
class ContainerPositionPanel extends StatelessWidget {
  const ContainerPositionPanel({
    required this.controller,
    this.maxHeight = double.infinity,
    this.expand = false,
    super.key,
  });

  final AppController controller;

  /// Ceiling handed down by the host layout. A docked panel sits in a Column
  /// that gives non-flex children unbounded height, so without this the panel
  /// keeps its preferred size and overflows the moment status banners appear
  /// above it — which is precisely when the operator needs both to be visible.
  final double maxHeight;

  /// True when the panel owns a column of its own and should use all of it.
  /// Docked at the bottom it stops at its preferred height instead, so the
  /// part selection above keeps the rest.
  final bool expand;

  /// A card narrower than this cannot hold a two-line action label at the
  /// terminal type scale without ellipsing it.
  static const double _minCardWidth = 268;

  /// Beyond this a single position just stretches into empty space.
  static const double _maxCardWidth = 520;

  @override
  Widget build(BuildContext context) {
    if (controller.containerPositionsLoading &&
        controller.containerPositions.isEmpty) {
      return const SizedBox(
        height: 104,
        child: Center(child: CircularProgressIndicator()),
      );
    }
    if (controller.containerPositionsError case final error?) {
      return Padding(
        padding: const EdgeInsets.all(12),
        child: Row(
          children: [
            Expanded(child: Text(error)),
            TextButton(
              key: const Key('retry-container-positions'),
              onPressed: controller.retryContainerPositions,
              child: const Text('Yenile'),
            ),
          ],
        ),
      );
    }
    final positions = controller.containerPositions;
    if (positions.isEmpty) {
      return const Padding(
        padding: EdgeInsets.all(14),
        child: Text(
          'Bu parça için doğrulanmış kasa konumu bulunmuyor.',
          key: Key('no-container-positions'),
          textAlign: TextAlign.center,
        ),
      );
    }

    final gutter = PdaScale.gutter(context);
    // The ceiling wraps the whole panel, padding and safe area included:
    // constraining only the inner Column let the panel exceed it by its own
    // padding, which is how a 29px overflow survived the first attempt.
    return ConstrainedBox(
      constraints: BoxConstraints(maxHeight: maxHeight),
      child: Material(
        key: const Key('container-position-panel'),
        elevation: 10,
        color: Theme.of(context).colorScheme.surface,
        child: SafeArea(
          top: false,
          child: Padding(
            padding: EdgeInsets.fromLTRB(
              gutter,
              gutter * 0.5,
              gutter,
              gutter * 0.5,
            ),
            child: Column(
              mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Flexible(
                  child: ConstrainedBox(
                    constraints: BoxConstraints(
                      maxHeight: expand
                          ? PdaScale.containerCardMaxHeight(context)
                          : PdaScale.containerPanelHeight(context),
                    ),
                    child: LayoutBuilder(
                      builder: (context, constraints) {
                        // Fill the terminal rather than leaving a 238px strip in
                        // the middle of a 1920px screen: widen the cards until
                        // they use the row, and only scroll once they cannot.
                        final available =
                            constraints.maxWidth -
                            gutter * (positions.length - 1);
                        final fitted = available / positions.length;
                        final cardWidth = fitted.isFinite
                            ? math.min(
                                _maxCardWidth,
                                math.max(_minCardWidth, fitted),
                              )
                            : _minCardWidth;
                        return ListView.separated(
                          key: const Key('container-position-list'),
                          scrollDirection: Axis.horizontal,
                          itemCount: positions.length,
                          separatorBuilder: (_, _) => SizedBox(width: gutter),
                          itemBuilder: (context, index) => SizedBox(
                            width: cardWidth,
                            child: _PositionCard(
                              controller: controller,
                              position: positions[index],
                            ),
                          ),
                        );
                      },
                    ),
                  ),
                ),
                if (expand) const Spacer(),
                if (!controller.isConfiguringBatchItem) ...[
                  SizedBox(height: gutter * 0.5),
                  _RemoveAllBar(controller: controller),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Variant-scoped bulk removal. Destructive and variant-wide, so it is stated
/// at readable size instead of the 10px caption it used to carry.
class _RemoveAllBar extends StatelessWidget {
  const _RemoveAllBar({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final gate = controller.mutationGate;
    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: PdaScale.gutter(context),
        vertical: PdaScale.gutter(context) * 0.6,
      ),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest.withValues(
          alpha: 0.45,
        ),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: theme.colorScheme.outlineVariant),
      ),
      child: Row(
        children: [
          Icon(
            Icons.inventory_2_outlined,
            size: 20,
            color: theme.colorScheme.primary,
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              controller.production.active != null
                  ? 'Mamul Kasa'
                  : 'Kasa Pozisyonları (${controller.context?.activeVariantCode ?? 'Aktif Varyant'})',
              style: TextStyle(
                fontWeight: FontWeight.w700,
                fontSize: PdaScale.body(context),
                color: const Color(0xFF0F172A),
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          if (!gate.canMutate)
            Text(
              gate.message ?? 'İşlem kilitli',
              style: TextStyle(
                fontSize: PdaScale.caption(context),
                color: theme.colorScheme.error,
                fontWeight: FontWeight.w600,
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
        ],
      ),
    );
  }
}

class _PositionCard extends StatelessWidget {
  const _PositionCard({required this.controller, required this.position});

  final AppController controller;
  final ContainerPosition position;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final total = controller.containerPositions.length;
    final pad = PdaScale.cardPadding(context);

    return Card(
      margin: EdgeInsets.zero,
      child: LayoutBuilder(
        builder: (context, constraints) {
          final actionHeight = math.max(
            48.0,
            constraints.maxHeight - pad * 2 - 52,
          );
          return SingleChildScrollView(
            child: ConstrainedBox(
              constraints: BoxConstraints(minHeight: constraints.maxHeight),
              child: Padding(
                padding: EdgeInsets.all(pad),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Row(
                      children: [
                        // The logical container number leads: it is what the operator
                        // was told on the line, and what the action keys are built on.
                        Container(
                          width: PdaScale.isCompact(context) ? 34 : 46,
                          height: PdaScale.isCompact(context) ? 34 : 46,
                          alignment: Alignment.center,
                          decoration: BoxDecoration(
                            color: theme.colorScheme.primary,
                            borderRadius: BorderRadius.circular(11),
                          ),
                          child: Text(
                            '${position.positionNumber}',
                            style: TextStyle(
                              color: theme.colorScheme.onPrimary,
                              fontSize: PdaScale.title(context) * 0.86,
                              fontWeight: FontWeight.w800,
                            ),
                          ),
                        ),
                        SizedBox(width: pad * 0.6),
                        Expanded(
                          flex: 3,
                          child: Text(
                            total > 1
                                ? '${position.label} (${position.positionNumber} / $total)'
                                : position.label,
                            style: TextStyle(
                              fontWeight: FontWeight.w800,
                              fontSize: PdaScale.title(context) * 0.8,
                            ),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        SizedBox(width: pad * 0.4),
                        // The state rides in the header rather than on its own row:
                        // every fixed block above the actions is height the buttons
                        // lose when the panel is squeezed by status banners.
                        Flexible(
                          flex: 2,
                          child: _StateBadge(position: position),
                        ),
                      ],
                    ),
                    SizedBox(height: pad * 0.5),
                    SizedBox(height: actionHeight, child: _actions(context)),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
  }

  /// The two business actions flex into whatever height the card has left, so
  /// the same card works in the docked panel and in a taller side column.
  Widget _actions(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final gap = PdaScale.gutter(context) * 0.45;
      final submittingFull = controller.isSubmittingContainerAction(
        position.positionNumber,
        'FULL',
      );
      final submittingEmpty = controller.isSubmittingContainerAction(
        position.positionNumber,
        'EMPTY',
      );
      final fullReady = position.fullAction.enabled || submittingFull;
      final emptyReady = position.emptyAction.enabled || submittingEmpty;
      final full = _BusinessActionButton(
        controller: controller,
        position: position,
        action: 'FULL',
        label: 'Kasam Doldu',
        availability: position.fullAction,
        icon: Icons.inventory,
      );
      final empty = _BusinessActionButton(
        controller: controller,
        position: position,
        action: 'EMPTY',
        label: 'Alan Boş',
        availability: position.emptyAction,
        icon: Icons.move_to_inbox_outlined,
      );
      if (!fullReady && !emptyReady) {
        final reason =
            position.fullAction.disabledMessage ??
            position.emptyAction.disabledMessage ??
            'Bu kasa için işlem uygun değil';
        return Center(
          child: Text(
            reason,
            key: Key('container-unavailable-${position.positionNumber}'),
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: PdaScale.caption(context),
              fontWeight: FontWeight.w700,
            ),
          ),
        );
      }
      if (fullReady && !emptyReady) return full;
      if (emptyReady && !fullReady) return empty;

      if (constraints.maxHeight < PdaScale.stackedActionsMinHeight(context)) {
        return Row(
          children: [
            Expanded(child: full),
            SizedBox(width: gap),
            Expanded(child: empty),
          ],
        );
      }

      return Column(
        children: [
          Expanded(flex: fullReady && !emptyReady ? 3 : 2, child: full),
          SizedBox(height: gap),
          Expanded(flex: emptyReady && !fullReady ? 3 : 2, child: empty),
        ],
      );
    },
  );
}

class _BusinessActionButton extends StatelessWidget {
  const _BusinessActionButton({
    required this.controller,
    required this.position,
    required this.action,
    required this.label,
    required this.availability,
    required this.icon,
  });

  final AppController controller;
  final ContainerPosition position;
  final String action;
  final String label;
  final ContainerActionAvailability availability;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final submitting = controller.isSubmittingContainerAction(
      position.positionNumber,
      action,
    );
    final enabled =
        availability.enabled &&
        controller.mutationGate.canMutate &&
        !submitting;

    final disabledReason = !controller.mutationGate.canMutate
        ? (controller.mutationGate.message ?? 'İşlem kilitli')
        : (!availability.enabled
              ? (availability.disabledMessage ??
                    'Bu kasa için işlem uygun değil')
              : null);

    final reasonHeight =
        PdaScale.caption(context) * 1.45 + PdaScale.gutter(context) * 0.25;

    return LayoutBuilder(
      builder: (context, constraints) {
        // Below this the printed reason would push the button under a usable
        // touch target, so it falls back to the tooltip rather than overflow.
        final showReason =
            !enabled &&
            !submitting &&
            disabledReason != null &&
            constraints.maxHeight >= 48 + reasonHeight;
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Expanded(
              child: Tooltip(
                key: Key('container-${position.positionNumber}-$action'),
                message: enabled ? label : (disabledReason ?? label),
                child: ElevatedButton(
                  // The key names the LOGICAL business action, which under WP-33 is
                  // (machine, variant/layout, part, logical container number, action) — so the
                  // container number has to be part of it.
                  key: Key(
                    action == 'FULL'
                        ? 'direct-action-REMOVE_FULL-${position.positionNumber}'
                        : 'direct-action-BRING_EMPTY-${position.positionNumber}',
                  ),
                  onPressed: enabled
                      ? () => controller.submitContainerAction(position, action)
                      : null,
                  style: ElevatedButton.styleFrom(
                    minimumSize: Size.zero,
                    backgroundColor: enabled
                        ? (action == 'FULL'
                              ? const Color(0xFFC2410C)
                              : const Color(0xFF0B4F6C))
                        : null,
                    foregroundColor: enabled ? Colors.white : null,
                    elevation: enabled ? 2 : 0,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(
                        PdaScale.actionRadius,
                      ),
                    ),
                    padding: EdgeInsets.symmetric(
                      horizontal: PdaScale.gutter(context) * 0.75,
                      vertical: 6,
                    ),
                    textStyle: TextStyle(
                      fontSize: enabled
                          ? PdaScale.primaryAction(context)
                          : PdaScale.secondaryAction(context),
                      fontWeight: enabled ? FontWeight.w800 : FontWeight.w600,
                    ),
                  ),
                  child: submitting
                      ? Row(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            SizedBox.square(
                              dimension: PdaScale.isCompact(context) ? 18 : 24,
                              child: const CircularProgressIndicator(
                                strokeWidth: 2.6,
                                color: Colors.white,
                              ),
                            ),
                            SizedBox(width: PdaScale.gutter(context) * 0.6),
                            const Flexible(
                              child: Text(
                                'Gönderiliyor',
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                          ],
                        )
                      : Row(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            Icon(
                              icon,
                              size: enabled
                                  ? (PdaScale.isCompact(context) ? 22 : 30)
                                  : (PdaScale.isCompact(context) ? 18 : 22),
                            ),
                            SizedBox(width: PdaScale.gutter(context) * 0.6),
                            Flexible(
                              child: Text(
                                label,
                                maxLines: 1,
                                textAlign: TextAlign.center,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                          ],
                        ),
                ),
              ),
            ),
            if (showReason)
              Padding(
                padding: EdgeInsets.only(top: PdaScale.gutter(context) * 0.25),
                child: Text(
                  disabledReason,
                  maxLines: 2,
                  textAlign: TextAlign.center,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: PdaScale.caption(context),
                    color: theme.colorScheme.error,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
          ],
        );
      },
    );
  }
}

class _StateBadge extends StatelessWidget {
  const _StateBadge({required this.position});

  final ContainerPosition position;

  @override
  Widget build(BuildContext context) {
    final state = position.transportState;
    final queue = position.queuePosition == null
        ? ''
        : ' · #${position.queuePosition}';
    final eta = _etaSuffix(position);
    final suffix = '$queue${eta == null ? '' : ' · $eta'}';
    final moving =
        state == 'REQUESTED' || state == 'IN_PROGRESS' || state == 'QUEUED';
    // Severity has to read from across the line. Every state used to render in
    // the same neutral chip, so "Hazır" and "Hata" looked identical.
    final colour = PdaStatusColors.of(state);

    final chip = Container(
      padding: EdgeInsets.symmetric(
        horizontal: PdaScale.gutter(context) * 0.75,
        vertical: PdaScale.gutter(context) * 0.4,
      ),
      decoration: BoxDecoration(
        color: PdaStatusColors.surfaceOf(state),
        borderRadius: BorderRadius.circular(99),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (moving)
            SizedBox.square(
              dimension: 12,
              child: CircularProgressIndicator(strokeWidth: 2, color: colour),
            )
          else
            Icon(Icons.circle, size: 10, color: colour),
          SizedBox(width: PdaScale.gutter(context) * 0.45),
          Flexible(
            child: Text(
              '${_stateLabel(state)}$suffix',
              style: TextStyle(
                fontSize: PdaScale.caption(context),
                fontWeight: FontWeight.w800,
                color: colour,
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ],
      ),
    );

    return Semantics(
      label: 'Taşıma durumu ${_stateLabel(state)}$suffix',
      child: Align(alignment: Alignment.centerLeft, child: chip),
    );
  }
}

/// Short ETA for the state chip. The queue screen spells it out in full;
/// inside a chip it has to stay to a couple of characters.
String? _etaSuffix(ContainerPosition position) {
  const running = {'REQUESTED', 'QUEUED', 'ROBOT_ASSIGNED', 'IN_PROGRESS'};
  if (!running.contains(position.transportState)) return null;
  if (position.etaState != 'AVAILABLE' || position.etaSeconds == null) {
    return null;
  }
  final minutes = (position.etaSeconds! / 60).ceil();
  return minutes < 1 ? '<1 dk' : '$minutes dk';
}

String _stateLabel(String state) => switch (state) {
  'READY' => 'Hazır',
  'WAITING_PREPARATION' => 'Hazırlık Bekleniyor',
  'REQUESTED' => 'İstendi',
  'QUEUED' => 'Sırada',
  'ROBOT_ASSIGNED' => 'Robot atandı',
  'IN_PROGRESS' => 'Devam ediyor',
  'COMPLETED' => 'Tamamlandı',
  'ERROR' => 'Hata',
  _ => 'Güncelleniyor',
};
