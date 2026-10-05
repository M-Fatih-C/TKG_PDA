import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../app/design_tokens.dart';
import 'widgets/warehouse_notification_widgets.dart';

/// The warehouse tablet's home: the two halves of the job. Semi-finished
/// preparation feeds machines; the finished-goods store receives what they
/// produce.
///
/// Both halves are live. Which physical locations the finished-goods store has
/// is still a customer decision, so that screen may legitimately show none.
class WarehouseHomeScreen extends StatelessWidget {
  const WarehouseHomeScreen({required this.controller, super.key});

  final AppController controller;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: controller,
    builder: (context, _) => Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('Depo'),
            if (controller.user case final user?)
              Text(
                '${user.fullName.trim().isNotEmpty ? user.fullName : user.username}'
                ' (Sicil: ${user.employeeNo?.trim().isNotEmpty == true ? user.employeeNo! : "—"})',
                key: const Key('warehouse-user-banner'),
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.normal,
                ),
                overflow: TextOverflow.ellipsis,
              ),
          ],
        ),
        actions: [
          WarehouseNotificationBell(
            coordinator: controller.warehouseNotifications,
            onNavigate: (stage) {
              if (stage == AppStage.preparationAreas &&
                  controller.canAccessSemiFinishedWarehouse) {
                controller.enterPreparationAreas();
              } else if (stage == AppStage.finishedGoodsStore &&
                  controller.canAccessFinishedGoodsWarehouse) {
                controller.enterFinishedGoodsStore();
              }
            },
          ),
          if (controller.canAccessSemiFinishedWarehouse)
            IconButton(
              key: const Key('open-warehouse-preparation'),
              tooltip: 'Depo Hazırlık Konsolu',
              onPressed: controller.busy
                  ? null
                  : controller.enterWarehousePreparation,
              icon: const Icon(Icons.inventory_2_outlined),
            ),
          IconButton(
            key: const Key('logout-button'),
            tooltip: 'Çıkış',
            onPressed: controller.isMutationInFlight ? null : controller.logout,
            icon: const Icon(Icons.logout),
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            WarehouseNotificationBanner(
              coordinator: controller.warehouseNotifications,
              onNavigate: (stage) {
                if (stage == AppStage.preparationAreas &&
                    controller.canAccessSemiFinishedWarehouse) {
                  controller.enterPreparationAreas();
                } else if (stage == AppStage.finishedGoodsStore &&
                    controller.canAccessFinishedGoodsWarehouse) {
                  controller.enterFinishedGoodsStore();
                }
              },
            ),
            Expanded(
              child: Padding(
                padding: EdgeInsets.all(PdaScale.gutter(context)),
                child: Builder(
                  builder: (context) {
                    final canSemi = controller.canAccessSemiFinishedWarehouse;
                    final canStore = controller.canAccessFinishedGoodsWarehouse;
                    final tiles = <Widget>[
                      if (canSemi)
                        Expanded(
                          child: _HomeTile(
                            actionKey: const Key('open-preparation-areas'),
                            icon: Icons.inventory_2_outlined,
                            title: 'Yarı Mamul Hazırlık',
                            description:
                                'Makinelerden gelen talepleri dört hazırlık alanında hazırlayın.',
                            color: PdaStatusColors.running,
                            onPressed: controller.enterPreparationAreas,
                          ),
                        ),
                      if (canSemi && canStore)
                        SizedBox(height: PdaScale.gutter(context)),
                      if (canStore)
                        Expanded(
                          child: _HomeTile(
                            actionKey: const Key('open-finished-goods'),
                            icon: Icons.warehouse_outlined,
                            title: 'Bitmiş Ürün Deposu',
                            description:
                                'Robotun getirdiği bitmiş ürünleri görün ve aldığınız alanları bildirin.',
                            color: PdaStatusColors.done,
                            onPressed: controller.enterFinishedGoodsStore,
                          ),
                        ),
                    ];

                    if (tiles.isEmpty) {
                      return const Center(
                        child: Text(
                          'Bu ekran için görüntüleme yetkiniz bulunmuyor.',
                          key: Key('warehouse-no-permission'),
                        ),
                      );
                    }

                    return Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: tiles,
                    );
                  },
                ),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _HomeTile extends StatelessWidget {
  const _HomeTile({
    required this.actionKey,
    required this.icon,
    required this.title,
    required this.description,
    required this.color,
    required this.onPressed,
  });

  final Key actionKey;
  final IconData icon;
  final String title;
  final String description;
  final Color color;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    return Material(
      key: actionKey,
      color: Colors.white,
      elevation: enabled ? 3 : 1,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(18),
        side: BorderSide(
          color: enabled ? color.withValues(alpha: 0.4) : const Color(0xFFCBD5E1),
          width: enabled ? 2 : 1,
        ),
      ),
      child: InkWell(
        onTap: onPressed,
        borderRadius: BorderRadius.circular(18),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
          child: Row(
            children: [
              Container(
                width: 76,
                height: 76,
                decoration: BoxDecoration(
                  color: color.withValues(alpha: enabled ? 0.12 : 0.06),
                  borderRadius: BorderRadius.circular(18),
                  border: Border.all(
                    color: color.withValues(alpha: enabled ? 0.3 : 0.15),
                    width: 1.5,
                  ),
                ),
                child: Icon(
                  icon,
                  size: 42,
                  color: enabled ? color : Colors.grey.shade400,
                ),
              ),
              const SizedBox(width: 24),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(
                      title,
                      style: TextStyle(
                        fontSize: PdaScale.title(context) * 1.08,
                        fontWeight: FontWeight.w900,
                        color: const Color(0xFF0F172A),
                        letterSpacing: -0.5,
                      ),
                    ),
                    const SizedBox(height: 6),
                    Text(
                      description,
                      style: TextStyle(
                        fontSize: PdaScale.body(context),
                        color: const Color(0xFF475569),
                        fontWeight: FontWeight.w500,
                        height: 1.35,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 16),
              if (enabled)
                Container(
                  width: 48,
                  height: 48,
                  decoration: BoxDecoration(
                    color: color.withValues(alpha: 0.1),
                    shape: BoxShape.circle,
                  ),
                  child: Icon(
                    Icons.arrow_forward_rounded,
                    color: color,
                    size: 26,
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
