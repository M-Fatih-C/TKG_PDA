import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../data/models/pda_models.dart';
import '../../shared/widgets/feedback_banners.dart';

class MachineListScreen extends StatelessWidget {
  const MachineListScreen({required this.controller, super.key});

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
            const Text('Makine Seçimi'),
            if (controller.user case final user?)
              Text(
                '${user.fullName.trim().isNotEmpty ? user.fullName : user.username} (Sicil: ${user.employeeNo?.trim().isNotEmpty == true ? user.employeeNo! : "—"})',
                key: const Key('operator-user-banner'),
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.normal,
                ),
                overflow: TextOverflow.ellipsis,
              ),
          ],
        ),
        actions: [
          IconButton(
            key: const Key('open-operator-queue'),
            tooltip: 'Görevler',
            onPressed: controller.busy ? null : controller.enterTasks,
            icon: const Icon(Icons.format_list_numbered),
          ),
          if (controller.canAccessAnyWarehouse) ...[
            IconButton(
              key: const Key('open-warehouse-home'),
              tooltip: 'Depo',
              onPressed: controller.busy ? null : controller.enterWarehouseHome,
              icon: const Icon(Icons.warehouse_outlined),
            ),
          ],
          PopupMenuButton<String>(
            tooltip: 'Diğer',
            onSelected: (value) {
              if (value == 'refresh') controller.reloadMachines();
            },
            itemBuilder: (context) => const [
              PopupMenuItem(value: 'refresh', child: Text('Yenile')),
            ],
          ),
          TextButton.icon(
            key: const Key('logout-button'),
            onPressed: controller.isMutationInFlight ? null : controller.logout,
            icon: const Icon(Icons.logout, color: Colors.white),
            label: const Text('Çıkış', style: TextStyle(color: Colors.white)),
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            if (controller.offline) const OfflineBanner(),
            if (controller.problem case final problem?)
              ProblemBanner(problem: problem),
            if (controller.uiNotice case final notice?)
              if (notice.persistent) NoticeBanner(notice: notice),
            Expanded(
              child: controller.busy && controller.machines.isEmpty
                  ? const Center(child: CircularProgressIndicator())
                  : controller.machines.isEmpty
                  ? RefreshIndicator(
                      onRefresh: controller.reloadMachines,
                      child: ListView(
                        key: const Key('machine-list-empty'),
                        padding: const EdgeInsets.all(24),
                        children: const [
                          SizedBox(height: 60),
                          Icon(
                            Icons.precision_manufacturing_outlined,
                            size: 56,
                            color: Colors.grey,
                          ),
                          SizedBox(height: 16),
                          Text(
                            'Size tanımlı makine bulunamadı.',
                            textAlign: TextAlign.center,
                            style: TextStyle(
                              fontSize: 17,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          SizedBox(height: 8),
                          Text(
                            'Erişim gerekiyorsa yöneticinizle iletişime geçin.',
                            textAlign: TextAlign.center,
                          ),
                        ],
                      ),
                    )
                  : RefreshIndicator(
                      onRefresh: controller.reloadMachines,
                      child: Center(
                        child: ConstrainedBox(
                          constraints: const BoxConstraints(maxWidth: 1080),
                          child: ListView.separated(
                            key: const Key('machine-list'),
                            padding: const EdgeInsets.symmetric(
                              horizontal: 20,
                              vertical: 20,
                            ),
                            itemCount: controller.machines.length,
                            separatorBuilder: (_, _) =>
                                const SizedBox(height: 14),
                            itemBuilder: (context, index) => _MachineCard(
                              machine: controller.machines[index],
                              enabled: !controller.busy,
                              onTap: controller.selectMachine,
                            ),
                          ),
                        ),
                      ),
                    ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _MachineCard extends StatelessWidget {
  const _MachineCard({
    required this.machine,
    required this.enabled,
    required this.onTap,
  });

  final MachineSummary machine;
  final bool enabled;
  final ValueChanged<MachineSummary> onTap;

  @override
  Widget build(BuildContext context) {
    final canOperate = machine.permissions.canOperate;
    return Card(
      key: Key('machine-${machine.id}'),
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(
          color: canOperate ? const Color(0xFFCBD5E1) : const Color(0xFFE2E8F0),
          width: canOperate ? 1.5 : 1,
        ),
      ),
      child: InkWell(
        onTap: enabled ? () => onTap(machine) : null,
        borderRadius: BorderRadius.circular(16),
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: Row(
            children: [
              Container(
                width: 64,
                height: 64,
                decoration: BoxDecoration(
                  color: canOperate
                      ? const Color(0xFF0B4F6C)
                      : const Color(0xFFE2E8F0),
                  borderRadius: BorderRadius.circular(14),
                  boxShadow: canOperate
                      ? [
                          BoxShadow(
                            color: const Color(
                              0xFF0B4F6C,
                            ).withValues(alpha: 0.25),
                            blurRadius: 8,
                            offset: const Offset(0, 3),
                          ),
                        ]
                      : null,
                ),
                child: Icon(
                  Icons.precision_manufacturing_rounded,
                  size: 34,
                  color: canOperate ? Colors.white : const Color(0xFF94A3B8),
                ),
              ),
              const SizedBox(width: 20),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Wrap(
                      crossAxisAlignment: WrapCrossAlignment.center,
                      spacing: 8,
                      runSpacing: 4,
                      children: [
                        Text(
                          machine.code,
                          style: TextStyle(
                            fontSize: 21,
                            fontWeight: FontWeight.w900,
                            letterSpacing: -0.5,
                            color: canOperate
                                ? const Color(0xFF0F172A)
                                : const Color(0xFF64748B),
                          ),
                        ),
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 8,
                            vertical: 2,
                          ),
                          decoration: BoxDecoration(
                            color: canOperate
                                ? const Color(
                                    0xFF16835D,
                                  ).withValues(alpha: 0.12)
                                : const Color(
                                    0xFFC26D16,
                                  ).withValues(alpha: 0.12),
                            borderRadius: BorderRadius.circular(6),
                            border: Border.all(
                              color: canOperate
                                  ? const Color(
                                      0xFF16835D,
                                    ).withValues(alpha: 0.3)
                                  : const Color(
                                      0xFFC26D16,
                                    ).withValues(alpha: 0.3),
                            ),
                          ),
                          child: Text(
                            canOperate ? 'Üretime Hazır' : 'Yetki Gerekli',
                            style: TextStyle(
                              fontSize: 12,
                              fontWeight: FontWeight.w700,
                              color: canOperate
                                  ? const Color(0xFF16835D)
                                  : const Color(0xFFC26D16),
                            ),
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 4),
                    Text(
                      machine.displayName,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w500,
                        color: Color(0xFF475569),
                      ),
                    ),
                    if (!canOperate) ...[
                      const SizedBox(height: 8),
                      Row(
                        children: [
                          const Icon(
                            Icons.info_outline,
                            size: 16,
                            color: Color(0xFF9A3412),
                          ),
                          const SizedBox(width: 6),
                          Expanded(
                            child: Text(
                              'Bu makinede taşıma işlemi yapamazsınız.',
                              key: Key(
                                'machine-unavailable-reason-${machine.id}',
                              ),
                              style: const TextStyle(
                                color: Color(0xFF9A3412),
                                fontWeight: FontWeight.w700,
                                fontSize: 13,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: 12),
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: canOperate
                      ? const Color(0xFFF1F5F9)
                      : Colors.transparent,
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(
                  Icons.arrow_forward_ios_rounded,
                  size: 18,
                  color: canOperate
                      ? const Color(0xFF0B4F6C)
                      : const Color(0xFFCBD5E1),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
