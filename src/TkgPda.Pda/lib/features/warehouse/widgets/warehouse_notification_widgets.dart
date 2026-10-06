import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../../app/app_controller.dart' show AppStage;
import '../../../shared/widgets/pda_empty_state.dart';
import '../warehouse_notification_coordinator.dart';

/// Floating toast/banner displayed briefly at the top of the warehouse tablet.
///
/// Gives immediate visual feedback when:
/// 1. A new preparation demand arrives from a machine.
/// 2. A new finished good arrives in the store.
/// 3. A capacity threshold is reached.
class WarehouseNotificationBanner extends StatelessWidget {
  const WarehouseNotificationBanner({
    required this.coordinator,
    required this.onNavigate,
    super.key,
  });

  final WarehouseNotificationCoordinator coordinator;
  final void Function(AppStage stage) onNavigate;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: coordinator,
    builder: (context, _) {
      final banner = coordinator.activeBanner;
      if (banner == null) return const SizedBox.shrink();

      final (color, icon, tag) = _styleFor(banner.type);

      return AnimatedSlide(
        duration: const Duration(milliseconds: 250),
        offset: Offset.zero,
        curve: Curves.easeOutCubic,
        child: Container(
          key: const Key('warehouse-notification-banner'),
          margin: const EdgeInsets.fromLTRB(16, 8, 16, 8),
          decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: color.withValues(alpha: 0.5), width: 1.8),
            boxShadow: [
              BoxShadow(
                color: color.withValues(alpha: 0.18),
                blurRadius: 16,
                offset: const Offset(0, 4),
              ),
              BoxShadow(
                color: const Color(0x1A000000),
                blurRadius: 8,
                offset: const Offset(0, 2),
              ),
            ],
          ),
          child: Material(
            color: Colors.transparent,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              child: Row(
                children: [
                  Container(
                    width: 44,
                    height: 44,
                    decoration: BoxDecoration(
                      color: color.withValues(alpha: 0.12),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Icon(icon, color: color, size: 24),
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Row(
                          children: [
                            Container(
                              padding: const EdgeInsets.symmetric(
                                horizontal: 6,
                                vertical: 2,
                              ),
                              decoration: BoxDecoration(
                                color: color,
                                borderRadius: BorderRadius.circular(4),
                              ),
                              child: Text(
                                tag,
                                style: const TextStyle(
                                  fontSize: 10,
                                  fontWeight: FontWeight.w800,
                                  color: Colors.white,
                                  letterSpacing: 0.4,
                                ),
                              ),
                            ),
                            const SizedBox(width: 8),
                            Expanded(
                              child: Text(
                                banner.title,
                                style: const TextStyle(
                                  fontWeight: FontWeight.w800,
                                  fontSize: 14,
                                  color: Color(0xFF0F172A),
                                ),
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                            Text(
                              DateFormat('HH:mm:ss').format(banner.timestamp),
                              style: const TextStyle(
                                fontSize: 11,
                                color: Color(0xFF64748B),
                                fontWeight: FontWeight.w500,
                              ),
                            ),
                          ],
                        ),
                        const SizedBox(height: 4),
                        Text(
                          banner.message,
                          style: const TextStyle(
                            fontSize: 13,
                            color: Color(0xFF334155),
                            fontWeight: FontWeight.w500,
                          ),
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 12),
                  FilledButton(
                    key: const Key('warehouse-notification-action'),
                    style: FilledButton.styleFrom(
                      backgroundColor: color,
                      foregroundColor: Colors.white,
                      padding: const EdgeInsets.symmetric(
                        horizontal: 14,
                        vertical: 8,
                      ),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(8),
                      ),
                      textStyle: const TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    onPressed: () {
                      coordinator.dismissActiveBanner();
                      coordinator.markRead(banner.id);
                      onNavigate(banner.targetStage);
                    },
                    child: const Text('Aç'),
                  ),
                  const SizedBox(width: 4),
                  IconButton(
                    key: const Key('warehouse-notification-dismiss'),
                    tooltip: 'Kapat',
                    iconSize: 20,
                    color: const Color(0xFF94A3B8),
                    onPressed: coordinator.dismissActiveBanner,
                    icon: const Icon(Icons.close),
                  ),
                ],
              ),
            ),
          ),
        ),
      );
    },
  );

  static (Color, IconData, String) _styleFor(WarehouseNotificationType type) =>
      switch (type) {
        WarehouseNotificationType.demand => (
          const Color(0xFF2563EB),
          Icons.move_to_inbox_rounded,
          'YENİ TALEP',
        ),
        WarehouseNotificationType.delivery => (
          const Color(0xFF16A34A),
          Icons.inventory_2_rounded,
          'YENİ TESLİMAT',
        ),
        WarehouseNotificationType.capacity => (
          const Color(0xFFEA580C),
          Icons.warning_amber_rounded,
          'KAPASİTE UYARISI',
        ),
      };
}

/// Notification bell icon action with unread badge to place inside AppBar actions.
class WarehouseNotificationBell extends StatelessWidget {
  const WarehouseNotificationBell({
    required this.coordinator,
    required this.onNavigate,
    super.key,
  });

  final WarehouseNotificationCoordinator coordinator;
  final void Function(AppStage stage) onNavigate;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: coordinator,
    builder: (context, _) {
      final unread = coordinator.unreadCount;
      return Stack(
        alignment: Alignment.center,
        children: [
          IconButton(
            key: const Key('warehouse-notifications-button'),
            tooltip: 'Bildirimler',
            icon: Icon(
              unread > 0
                  ? Icons.notifications_active_rounded
                  : Icons.notifications_outlined,
            ),
            onPressed: () => _openSheet(context),
          ),
          if (unread > 0)
            Positioned(
              right: 6,
              top: 6,
              child: Container(
                key: const Key('warehouse-notification-badge'),
                padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 2),
                decoration: BoxDecoration(
                  color: const Color(0xFFDC2626),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: Colors.white, width: 1.5),
                ),
                constraints: const BoxConstraints(minWidth: 18, minHeight: 18),
                child: Text(
                  unread > 9 ? '9+' : '$unread',
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                    color: Colors.white,
                    fontSize: 10,
                    fontWeight: FontWeight.w900,
                    height: 1,
                  ),
                ),
              ),
            ),
        ],
      );
    },
  );

  void _openSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (sheetContext) => WarehouseNotificationSheet(
        coordinator: coordinator,
        onNavigate: (stage) {
          Navigator.of(sheetContext).pop();
          onNavigate(stage);
        },
      ),
    );
  }
}

/// Notification drawer / bottom sheet showing the history of warehouse notifications.
class WarehouseNotificationSheet extends StatelessWidget {
  const WarehouseNotificationSheet({
    required this.coordinator,
    required this.onNavigate,
    super.key,
  });

  final WarehouseNotificationCoordinator coordinator;
  final void Function(AppStage stage) onNavigate;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: coordinator,
    builder: (context, _) => Container(
      key: const Key('warehouse-notification-sheet'),
      constraints: BoxConstraints(
        maxHeight: MediaQuery.sizeOf(context).height * 0.85,
        maxWidth: 600,
      ),
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 16),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(20),
        boxShadow: const [
          BoxShadow(
            color: Color(0x33000000),
            blurRadius: 24,
            offset: Offset(0, 8),
          ),
        ],
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          _Header(coordinator: coordinator),
          const Divider(height: 1),
          Flexible(
            child: coordinator.notifications.isEmpty
                ? const PdaEmptyState(
                    icon: Icons.notifications_none_rounded,
                    title: 'Bildirim Yok',
                    subtitle:
                        'Makinelerden yeni talep, depoya yeni ürün teslimatı veya alan kapasite uyarıları burada listelenecektir.',
                    textKey: Key('warehouse-notifications-empty'),
                  )
                : ListView.separated(
                    key: const Key('warehouse-notification-list'),
                    padding: const EdgeInsets.symmetric(
                      horizontal: 16,
                      vertical: 12,
                    ),
                    itemCount: coordinator.notifications.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 8),
                    itemBuilder: (context, index) => _NotificationTile(
                      notification: coordinator.notifications[index],
                      onTap: () {
                        final notification = coordinator.notifications[index];
                        coordinator.markRead(notification.id);
                        onNavigate(notification.targetStage);
                      },
                    ),
                  ),
          ),
        ],
      ),
    ),
  );
}

class _Header extends StatelessWidget {
  const _Header({required this.coordinator});

  final WarehouseNotificationCoordinator coordinator;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 12, 8, 8),
    child: Row(
      children: [
        const Icon(
          Icons.notifications_outlined,
          size: 20,
          color: Color(0xFF0F172A),
        ),
        const SizedBox(width: 6),
        const Text(
          'Bildirimler',
          style: TextStyle(
            fontSize: 16,
            fontWeight: FontWeight.w800,
            color: Color(0xFF0F172A),
          ),
        ),
        const Spacer(),
        IconButton(
          key: const Key('warehouse-notification-sound-toggle'),
          tooltip: coordinator.soundEnabled
              ? 'Sesli Uyarılar: Açık'
              : 'Sesli Uyarılar: Kapalı',
          iconSize: 20,
          padding: const EdgeInsets.all(6),
          constraints: const BoxConstraints(),
          icon: Icon(
            coordinator.soundEnabled
                ? Icons.volume_up_rounded
                : Icons.volume_off_rounded,
            color: coordinator.soundEnabled
                ? const Color(0xFF2563EB)
                : const Color(0xFF94A3B8),
          ),
          onPressed: coordinator.toggleSound,
        ),
        const SizedBox(width: 4),
        if (coordinator.hasUnread)
          TextButton(
            key: const Key('warehouse-mark-all-read'),
            style: TextButton.styleFrom(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
              minimumSize: const Size(0, 32),
              textStyle: const TextStyle(fontSize: 12),
            ),
            onPressed: coordinator.markAllRead,
            child: const Text('Tümünü Oku'),
          ),
        if (coordinator.notifications.isNotEmpty)
          IconButton(
            key: const Key('warehouse-clear-notifications'),
            tooltip: 'Temizle',
            iconSize: 20,
            padding: const EdgeInsets.all(6),
            constraints: const BoxConstraints(),
            icon: const Icon(Icons.delete_sweep_outlined, size: 20),
            onPressed: coordinator.clearAll,
          ),
      ],
    ),
  );
}

class _NotificationTile extends StatelessWidget {
  const _NotificationTile({required this.notification, required this.onTap});

  final WarehouseNotification notification;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final (color, icon, tag) = WarehouseNotificationBanner._styleFor(
      notification.type,
    );
    final isUnread = !notification.isRead;

    return Container(
      decoration: BoxDecoration(
        color: isUnread
            ? color.withValues(alpha: 0.04)
            : const Color(0xFFF8FAFC),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(
          color: isUnread
              ? color.withValues(alpha: 0.35)
              : const Color(0xFFE2E8F0),
          width: isUnread ? 1.5 : 1,
        ),
      ),
      child: ListTile(
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
        leading: Container(
          width: 40,
          height: 40,
          decoration: BoxDecoration(
            color: color.withValues(alpha: 0.12),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Icon(icon, color: color, size: 22),
        ),
        title: Row(
          children: [
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: color,
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                tag,
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w800,
                  color: Colors.white,
                ),
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                notification.title,
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: isUnread ? FontWeight.w800 : FontWeight.w600,
                  color: const Color(0xFF0F172A),
                ),
                overflow: TextOverflow.ellipsis,
              ),
            ),
            if (isUnread)
              Container(
                width: 8,
                height: 8,
                margin: const EdgeInsets.only(left: 6),
                decoration: BoxDecoration(color: color, shape: BoxShape.circle),
              ),
          ],
        ),
        subtitle: Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                notification.message,
                style: const TextStyle(
                  fontSize: 12.5,
                  color: Color(0xFF475569),
                  height: 1.3,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                DateFormat('HH:mm:ss').format(notification.timestamp),
                style: const TextStyle(
                  fontSize: 11,
                  color: Color(0xFF94A3B8),
                  fontWeight: FontWeight.w500,
                ),
              ),
            ],
          ),
        ),
        trailing: OutlinedButton(
          key: Key('notification-go-to-${notification.id}'),
          style: OutlinedButton.styleFrom(
            foregroundColor: color,
            side: BorderSide(color: color.withValues(alpha: 0.5)),
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
            minimumSize: const Size(0, 36),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(8),
            ),
          ),
          onPressed: onTap,
          child: const Text(
            'Git',
            style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700),
          ),
        ),
      ),
    );
  }
}
