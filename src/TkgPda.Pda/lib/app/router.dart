import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../features/device_setup/device_setup_screen.dart';
import '../features/login/login_screen.dart';
import '../features/machines/machine_list_screen.dart';
import '../features/operation/operation_screen.dart';
import '../features/tasks/operator_queue_screen.dart';
import '../features/warehouse/preparation_areas_screen.dart';
import '../features/warehouse/store_screen.dart';
import '../features/warehouse/warehouse_home_screen.dart';
import '../data/models/pda_models.dart';
import 'app_controller.dart';

GoRouter buildRouter(AppController controller) => GoRouter(
  initialLocation: '/starting',
  refreshListenable: controller,
  redirect: (context, state) {
    final stage = controller.stage;
    if (stage == AppStage.preparationAreas &&
        !controller.canAccessSemiFinishedWarehouse) {
      return controller.canAccessAnyWarehouse ? '/warehouse' : '/machines';
    }
    if (stage == AppStage.warehousePreparation &&
        !controller.canAccessSemiFinishedWarehouse) {
      return controller.canAccessAnyWarehouse ? '/warehouse' : '/machines';
    }
    if (stage == AppStage.finishedGoodsStore &&
        !controller.canAccessFinishedGoodsWarehouse) {
      return controller.canAccessAnyWarehouse ? '/warehouse' : '/machines';
    }
    if (stage == AppStage.warehouseHome && !controller.canAccessAnyWarehouse) {
      return '/machines';
    }

    final target = switch (stage) {
      AppStage.starting => '/starting',
      AppStage.deviceSetup => '/device-setup',
      AppStage.login => '/login',
      AppStage.machines => '/machines',
      AppStage.operation =>
        controller.selectedMachine == null
            ? '/machines'
            : '/machines/${controller.selectedMachine!.id}',
      AppStage.tasks => '/tasks',
      AppStage.warehousePreparation => '/warehouse/preparation-areas',
      AppStage.warehouseHome => '/warehouse',
      AppStage.preparationAreas => '/warehouse/preparation-areas',
      AppStage.finishedGoodsStore => '/warehouse/store',
    };
    return state.matchedLocation == target ? null : target;
  },
  routes: [
    GoRoute(
      path: '/starting',
      builder: (context, state) =>
          const Scaffold(body: Center(child: CircularProgressIndicator())),
    ),
    GoRoute(
      path: '/device-setup',
      builder: (context, state) => DeviceSetupScreen(controller: controller),
    ),
    GoRoute(
      path: '/login',
      builder: (context, state) => LoginScreen(controller: controller),
    ),
    GoRoute(
      path: '/machines',
      builder: (context, state) => MachineListScreen(controller: controller),
    ),
    GoRoute(
      path: '/machines/:machineId',
      builder: (context, state) => OperationScreen(controller: controller),
    ),
    GoRoute(
      path: '/tasks',
      builder: (context, state) => OperatorQueueScreen(controller: controller),
    ),
    GoRoute(
      path: '/warehouse-preparation',
      redirect: (context, state) => '/warehouse/preparation-areas',
    ),
    GoRoute(
      path: '/warehouse',
      builder: (context, state) => WarehouseHomeScreen(controller: controller),
    ),
    GoRoute(
      path: '/warehouse/store',
      builder: (context, state) => StoreScreen(
        coordinator: controller.store,
        onBack: controller.leaveFinishedGoodsStore,
        onNavigate: (stage) {
          if (stage == AppStage.tasks) {
            controller.enterTasks();
          } else if (stage == AppStage.preparationAreas &&
              controller.canAccessSemiFinishedWarehouse) {
            controller.enterPreparationAreas();
          }
        },
      ),
    ),
    GoRoute(
      path: '/warehouse/preparation-areas',
      builder: (context, state) => PreparationAreasScreen(
        coordinator: controller.preparation,
        operatorLabel: _operatorLabel(controller.user),
        onBack: controller.leavePreparationAreas,
        onNavigate: (stage) {
          if (stage == AppStage.tasks) {
            controller.enterTasks();
          } else if (stage == AppStage.finishedGoodsStore &&
              controller.canAccessFinishedGoodsWarehouse) {
            controller.enterFinishedGoodsStore();
          }
        },
      ),
    ),
  ],
);

String? _operatorLabel(AuthenticatedUser? user) {
  if (user == null) return null;
  final name = user.fullName.trim().isNotEmpty ? user.fullName : user.username;
  final sicil = user.employeeNo?.trim().isNotEmpty == true
      ? user.employeeNo!
      : '—';
  return '$name (Sicil: $sicil)';
}
