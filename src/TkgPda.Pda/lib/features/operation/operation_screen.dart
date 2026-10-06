import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../app/design_tokens.dart';
import '../../shared/widgets/feedback_banners.dart';
import 'container_position_panel.dart';
import 'part_first_screen.dart';
import 'transport_action_panel.dart';

/// Machine-scoped Operator workspace. Part/variant resolution and transport
/// actions share one page; there is no second physical-layout screen.
class OperationScreen extends StatelessWidget {
  const OperationScreen({required this.controller, super.key});

  final AppController controller;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: controller,
    builder: (context, _) => TransientNoticeHost(
      controller: controller,
      child: Scaffold(
        appBar: AppBar(
          automaticallyImplyLeading: !controller.isBoundLineTerminalOperator,
          leading: controller.isBoundLineTerminalOperator
              ? null
              : IconButton(
                  key: const Key('leave-machine'),
                  tooltip: 'Makine listesi',
                  onPressed: controller.busy ? null : controller.leaveMachine,
                  icon: const Icon(Icons.arrow_back),
                ),
          title: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                controller.selectedMachine == null
                    ? 'Parça ve İşlem'
                    : controller.production.active != null
                    ? controller.selectedMachine!.code
                    : '${controller.selectedMachine!.code} · Parça ve İşlem',
                overflow: TextOverflow.ellipsis,
              ),
              if (controller.user case final user?)
                Text(
                  '${user.fullName.trim().isNotEmpty ? user.fullName : user.username} (Sicil: ${user.employeeNo?.trim().isNotEmpty == true ? user.employeeNo! : "—"})',
                  key: const Key('operator-user-banner'),
                  style: const TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.normal,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
            ],
          ),
          actions: [
            KeyedSubtree(
              key: const Key('open-operator-queue'),
              child: IconButton(
                key: const Key('open-operator-queue-from-machine'),
                tooltip: 'Görevler',
                onPressed: controller.busy ? null : controller.enterTasks,
                icon: const Icon(Icons.format_list_numbered),
              ),
            ),
            PopupMenuButton<String>(
              tooltip: 'Diğer',
              onSelected: (value) {
                if (value == 'refresh') {
                  controller.refreshContext(showBusy: true);
                }
              },
              itemBuilder: (context) => const [
                PopupMenuItem(value: 'refresh', child: Text('Yenile')),
              ],
            ),
            Tooltip(
              message: 'Oturumu kapat',
              child: TextButton(
                key: const Key('logout-button'),
                onPressed: controller.isMutationInFlight
                    ? null
                    : controller.logout,
                child: const Text(
                  'Çıkış',
                  style: TextStyle(color: Colors.white),
                ),
              ),
            ),
          ],
        ),
        body: SafeArea(
          child: Column(
            children: [
              _StatusRegion(controller: controller),
              Expanded(child: _PartFirstWithBatchSkip(controller: controller)),
              if (controller.busy) const LinearProgressIndicator(minHeight: 5),
            ],
          ),
        ),
        bottomNavigationBar:
            controller.batchMode || controller.batchCart.isNotEmpty
            ? BatchCartTray(controller: controller)
            : null,
      ),
    ),
  );
}

/// Every status banner the workspace can raise, with a ceiling on the ones
/// that only report.
///
/// These are eight independent conditions and they used to stack without any
/// limit: offline plus an error plus a robot alarm plus an active transport
/// plus an ambiguous submission took roughly half the screen away from the
/// work, exactly when the operator most needed to see it.
///
/// The split matters. Reporting banners share one capped, scrollable region —
/// nothing is dropped, it just cannot starve the surface below. Banners that
/// ASK FOR A DECISION stay outside that cap and always render in full: an
/// ambiguous submission is resolved by choosing retry or refresh, and a choice
/// the operator has to scroll to find is a choice that does not get made.
class _StatusRegion extends StatelessWidget {
  const _StatusRegion({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      ConstrainedBox(
        constraints: BoxConstraints(
          maxHeight: PdaScale.statusRegionMaxHeight(context),
        ),
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (controller.offline) const OfflineBanner(),
              if (controller.problem case final problem?)
                ProblemBanner(problem: problem),
              if (controller.uiNotice case final notice?)
                if (notice.persistent) NoticeBanner(notice: notice),
              RobotAlarmBanner(
                summary: controller.robotAlarmSummary,
                error: controller.robotAlarmError,
                onRetry: controller.retryRobotAlarmSummary,
              ),
            ],
          ),
        ),
      ),
      if (controller.hasAmbiguousSubmission)
        _AmbiguousSubmissionBanner(controller: controller),
      if (controller.hasAmbiguousBatchSubmission)
        _AmbiguousBatchBanner(controller: controller),
      if (controller.isConfiguringBatchItem)
        _BatchConfiguringBanner(controller: controller),
    ],
  );
}

class _PartFirstWithBatchSkip extends StatelessWidget {
  const _PartFirstWithBatchSkip({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final selector = PartFirstSelector(controller: controller);
      final skip =
          controller.isConfiguringBatchItem &&
              (controller.compatibleVariants?.isEmpty ?? false)
          ? Padding(
              padding: const EdgeInsets.all(14),
              child: OutlinedButton.icon(
                key: const Key('skip-invalid-batch-item'),
                onPressed: controller.busy
                    ? null
                    : controller.skipInvalidBatchItem,
                icon: const Icon(Icons.remove_circle_outline),
                label: const Text('Bu Parçayı Kaldır ve Devam Et'),
              ),
            )
          : null;

      // Wide and 1024×600 terminals: alt parts on the left, Mamul Kasa then
      // active transports in the same right column. Phones stay one stack.
      // There is one transport section; it is not repeated under the page.
      final showMamul = controller.mamulActionsReady;
      final requests = controller.context?.activeTransportRequests ?? const [];
      final showTransports =
          requests.isNotEmpty || controller.production.active != null;
      final transports = OperatorActiveTransportList(
        requests: requests,
        controller: controller,
      );
      if (PdaScale.usesSideColumn(context)) {
        return Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Expanded(
              child: Column(
                children: [
                  Expanded(child: selector),
                  ?skip,
                ],
              ),
            ),
            if (showMamul || showTransports) ...[
              SizedBox(width: PdaScale.gutter(context)),
              SizedBox(
                width: (constraints.maxWidth * 0.42).clamp(300.0, 560.0),
                child: Column(
                  children: [
                    if (showMamul)
                      ContainerPositionPanel(controller: controller),
                    if (showMamul && showTransports)
                      SizedBox(height: PdaScale.gutter(context) * 0.4),
                    if (showTransports) Expanded(child: transports),
                  ],
                ),
              ),
            ],
          ],
        );
      }
      final transportHeight = requests.isEmpty
          ? 88.0
          : (constraints.maxHeight * 0.34).clamp(168.0, 240.0);
      return Column(
        children: [
          Expanded(child: selector),
          if (showMamul)
            ContainerPositionPanel(
              controller: controller,
              maxHeight: constraints.maxHeight * 0.38,
            ),
          ?skip,
          if (showTransports)
            SizedBox(height: transportHeight, child: transports),
        ],
      );
    },
  );
}

class _BatchConfiguringBanner extends StatelessWidget {
  const _BatchConfiguringBanner({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('batch-configuring-banner'),
    width: double.infinity,
    color: const Color(0xFFE0F2FE),
    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          'Grup: Parça ${controller.currentBatchItemPosition}/${controller.batchCart.length} yapılandırılıyor · '
          '${controller.currentBatchItem?.effectivePart.partCode ?? ''}',
          style: const TextStyle(fontWeight: FontWeight.w800),
        ),
        const SizedBox(height: 6),
        _BatchProgressDots(controller: controller),
      ],
    ),
  );
}

class _BatchProgressDots extends StatelessWidget {
  const _BatchProgressDots({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => Wrap(
    key: const Key('batch-progress-dots'),
    spacing: 6,
    runSpacing: 6,
    children: [
      for (final (index, item) in controller.batchCart.indexed)
        Tooltip(
          message: item.effectivePart.partCode,
          child: Container(
            key: Key('batch-progress-dot-$index'),
            width: 22,
            height: 22,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: item.isConfigured ? const Color(0xFF166534) : Colors.white,
              border: Border.all(
                color: index == controller.currentBatchItemPosition - 1
                    ? const Color(0xFFF59E0B)
                    : const Color(0xFF94A3B8),
                width: index == controller.currentBatchItemPosition - 1 ? 3 : 1,
              ),
            ),
            alignment: Alignment.center,
            child: item.isConfigured
                ? const Icon(Icons.check, size: 13, color: Colors.white)
                : Text(
                    '${index + 1}',
                    style: const TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
          ),
        ),
    ],
  );
}

class _AmbiguousSubmissionBanner extends StatelessWidget {
  const _AmbiguousSubmissionBanner({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('ambiguous-submission-banner'),
    color: const Color(0xFFFFEDD5),
    padding: const EdgeInsets.all(12),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text(
          'Son gönderimin sonucu belirsiz. Yeni anahtar üretilmeyecek.',
          style: TextStyle(fontWeight: FontWeight.w900),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          children: [
            OutlinedButton(
              key: const Key('retry-ambiguous'),
              onPressed: controller.busy
                  ? null
                  : controller.retryAmbiguousTransport,
              child: const Text('Aynı İsteği Yeniden Dene'),
            ),
            OutlinedButton(
              key: const Key('refresh-after-ambiguous'),
              onPressed: controller.busy
                  ? null
                  : () => controller.refreshContext(showBusy: true),
              child: const Text('Sunucu Durumunu Yenile'),
            ),
          ],
        ),
      ],
    ),
  );
}

class _AmbiguousBatchBanner extends StatelessWidget {
  const _AmbiguousBatchBanner({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('ambiguous-batch-banner'),
    color: const Color(0xFFFFEDD5),
    padding: const EdgeInsets.all(12),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text(
          'Son grup gönderiminin sonucu belirsiz. Yeni anahtar üretilmeyecek.',
          style: TextStyle(fontWeight: FontWeight.w900),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          children: [
            OutlinedButton(
              key: const Key('retry-ambiguous-batch'),
              onPressed: controller.busy
                  ? null
                  : controller.retryAmbiguousBatch,
              child: const Text('Aynı Grubu Yeniden Dene'),
            ),
            OutlinedButton(
              key: const Key('refresh-after-ambiguous-batch'),
              onPressed: controller.busy
                  ? null
                  : () => controller.refreshContext(showBusy: true),
              child: const Text('Sunucu Durumunu Yenile'),
            ),
          ],
        ),
      ],
    ),
  );
}

class BatchCartTray extends StatefulWidget {
  const BatchCartTray({required this.controller, super.key});

  final AppController controller;

  @override
  State<BatchCartTray> createState() => _BatchCartTrayState();
}

class _BatchCartTrayState extends State<BatchCartTray> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final controller = widget.controller;
    final cart = controller.batchCart;
    final theme = Theme.of(context);

    return Material(
      elevation: 8,
      color: theme.colorScheme.surfaceContainerHighest,
      child: SafeArea(
        top: false,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
              child: Row(
                children: [
                  Icon(
                    Icons.shopping_cart_outlined,
                    color: theme.colorScheme.primary,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: InkWell(
                      key: const Key('batch-cart-tray'),
                      onTap: cart.isEmpty
                          ? null
                          : () => setState(() => _expanded = !_expanded),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Text(
                                controller.production.active != null
                                    ? 'Toplu Alt Parça'
                                    : 'Toplu Taşıma Sepeti',
                                style: const TextStyle(
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                              const SizedBox(width: 6),
                              Container(
                                key: const Key('batch-cart-count'),
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 6,
                                  vertical: 2,
                                ),
                                decoration: BoxDecoration(
                                  color: theme.colorScheme.primary,
                                  borderRadius: BorderRadius.circular(10),
                                ),
                                child: Text(
                                  '${controller.production.active != null ? controller.selectedAltParts.length : cart.length}',
                                  key: const Key('selected-alt-count'),
                                  style: TextStyle(
                                    color: theme.colorScheme.onPrimary,
                                    fontSize: 12,
                                    fontWeight: FontWeight.w800,
                                  ),
                                ),
                              ),
                            ],
                          ),
                          Text(
                            controller.production.active != null
                                ? (controller.selectedAltParts.isEmpty
                                      ? 'Alt parçaları işaretleyin'
                                      : '${controller.selectedAltParts.length} Alt Parça Seçildi')
                                : cart.isEmpty
                                ? 'Parça seçip sepete ekleyin'
                                : '${cart.length} kayıt hazır',
                            style: TextStyle(
                              fontSize: PdaScale.caption(context),
                              color: theme.hintColor,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                  if (cart.isNotEmpty ||
                      (controller.production.active != null &&
                          controller.selectedAltParts.isNotEmpty)) ...[
                    TextButton(
                      key: const Key('batch-clear-cart'),
                      onPressed: controller.busy
                          ? null
                          : controller.exitBatchSelection,
                      child: Text(
                        controller.production.active != null
                            ? 'Vazgeç'
                            : 'Temizle',
                      ),
                    ),
                    const SizedBox(width: 4),
                    FilledButton.icon(
                      key: const Key('batch-submit'),
                      onPressed: controller.production.active != null
                          ? (controller.selectedAltParts.isNotEmpty &&
                                    !controller.busy
                                ? controller.submitSelectedAltParts
                                : null)
                          : controller.canReviewBatch && !controller.busy
                          ? controller.submitBatch
                          : null,
                      icon: const Icon(Icons.send, size: 16),
                      label: Text(
                        controller.production.active != null
                            ? '${controller.selectedAltParts.length} Alt Parçayı Gönder'
                            : 'Tümünü Gönder',
                      ),
                    ),
                  ],
                ],
              ),
            ),
            if (_expanded &&
                cart.isNotEmpty &&
                controller.production.active == null)
              Container(
                constraints: const BoxConstraints(maxHeight: 180),
                child: ListView.separated(
                  shrinkWrap: true,
                  padding: const EdgeInsets.fromLTRB(14, 0, 14, 10),
                  itemCount: cart.length,
                  separatorBuilder: (_, _) => const Divider(height: 1),
                  itemBuilder: (context, index) {
                    final item = cart[index];
                    final actionText = item.actionCode == 'FULL'
                        ? 'Dolu Kasa'
                        : 'Boş Kasa';
                    return ListTile(
                      dense: true,
                      contentPadding: EdgeInsets.zero,
                      title: Text(
                        '${item.effectivePart.partCode} · ${item.slotDisplayName}',
                        style: const TextStyle(fontWeight: FontWeight.w700),
                      ),
                      subtitle: Text(actionText),
                      trailing: IconButton(
                        key: Key('batch-remove-item-${item.cartItemId}'),
                        tooltip: 'Sepetten Çıkar',
                        icon: const Icon(
                          Icons.close,
                          size: 18,
                          color: Colors.redAccent,
                        ),
                        onPressed: () =>
                            controller.removePartFromBatch(item.cartItemId),
                      ),
                    );
                  },
                ),
              ),
          ],
        ),
      ),
    );
  }
}
