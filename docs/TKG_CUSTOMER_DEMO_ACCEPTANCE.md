# TKG production acceptance

This is the acceptance record for the real-time production system. RDS is the approved live robot execution layer. Sending a valid transport request to RDS is expected production behavior.

Branch: `agent/tkg-final-demo-hardening`

Implementation: `dba31e5c4769f6efcc0b35543ca277548a0f1878` and the internal-consistency follow-up on that branch.

Statuses: PASS, PARTIAL, CUSTOMER DATA REQUIRED, VENDOR API REQUIRED, CUSTOMER CONFIRMATION REQUIRED.

| Item | Status | Implementation | Tests | Remaining dependency |
| --- | --- | --- | --- | --- |
| 01 Alt Part button immediately creates a transport request | PASS | `POST /api/v1/machines/{machineId}/part-demands`. During active production Flutter `requestAltPart` creates the backend demand directly. The operator does not choose load, slot, or container. | `PartDemandApiTests`, `production_session_test.dart` | None for the direct request. |
| 02 Repeated same-part request removes the existing pallet, then brings the new one | PASS | `current_part_id` is the physical identity. The same requested part OCCUPIED+FULL becomes REMOVE_FULL then BRING_FULL. OCCUPIED+EMPTY becomes REMOVE_EMPTY then BRING_FULL. A different physical part is never removed as though it were the requested part. A safe vacant compatible area receives a direct BRING_FULL. Unknown or ambiguous occupancy fails closed. | `PartDemandRules.Select`, `PartDemandRulesTests`, `PartDemandApiTests` | None. |
| 03 Container screen is only for Mamul Kasa | PASS | Mamul Kasa is scoped to the active production main part, product group, variant, and verified layout. Alt Part selection and alt-part batch selection do not retarget it. | `production_session_test.dart`, `production_flow_test.dart` | None. |
| 04 Fixed-area pick/drop needs no extra operator confirmation | PASS | Alt Part click and Mamul Kasam Doldu / Alan Boş submit the business action directly. | `PartDemandApiTests`, `FinishedContainerFlowApiTests` | None. |
| 05 Mamul Kasam Doldu / Alan Boş creates the correct work order | PASS | OCCUPIED+EMPTY marks FULL and creates REMOVE_FULL. OCCUPIED+FULL creates REMOVE_FULL. VACANT creates BRING_EMPTY in WAITING_PREPARATION. UNKNOWN fails closed. | `FinishedContainerFlowApiTests` | Finished-goods destination mapping is still required before REMOVE_FULL can be created. |
| 06 Machine, warehouse, semi-finished, and finished-goods roles are separated | PASS | `LINE_OPERATOR`, `SEMI_FINISHED_WAREHOUSE_OPERATOR`, `FINISHED_GOODS_WAREHOUSE_OPERATOR`, legacy `WAREHOUSE_OPERATOR`, `SUPERVISOR`, `ADMIN`. | `WarehouseRoleAuthorizationApiTests` | None. |
| 07 Terminal-machine relationship comes from device binding | PASS | Server device-to-machine binding. The field terminal uses the bound machine. | `TerminalMachineBindingApiTests` | None. |
| 08 Active transports sit below the Mamul Kasa request area | PASS | `OperationScreen` renders `OperatorActiveTransportList` under the mamul workspace. | `production_session_test.dart`, active-transport widget coverage in the PDA suite | None. |
| 09 Alt Part requests and status are visible in active transports | PASS | Active transport cards include part code, description, ALT_PART badge, request number, operation, queue, readiness, lifecycle, elapsed time, and chain labels. | `ActiveTransportProjectionApiTests` | None. |
| 10 Each terminal shows only its machine tasks | PASS | Machine operational context is scoped to the bound machine. Global Tasks remains a separate fleet screen. | `TerminalMachineBindingApiTests`, `GlobalTaskVisibilityAndCancellationScopeApiTests` | None. |
| 11 Transport state updates and has a visual status bar | PASS | Segmented bar: TALEP, HAZIRLIK, ROBOT GÖREVİ, TAŞINIYOR, TAMAMLANDI. Stages light only from backend status and readiness. No robot percentage. | `ActiveTransportProjectionApiTests` | Live robot progress percentage is not available and is not invented. |
| 12 Production End priority comes from a configurable table | PARTIAL | `master.production_end_priorities`. Admin page `/production-end-priorities`. Unconfigured lookup returns `PRODUCTION_END_PRIORITY_NOT_CONFIGURED`. No numbers are seeded. | `OperationalEvidenceApiTests`, `TransportDurationMetricsTests` is unrelated; priority tests are the API tests | TKG has not supplied the numeric priorities or the exact task list Üretimi Bitir must create. Ending a session still only closes the session. |
| 13 Finished-goods operator sees areas, empty-container availability, and fill/empty actions | PASS | Store cards show code, display name, and unit load. Vacant is not treated as an empty container. `POST /api/v1/warehouse/store/locations/{id}/empty-container` records an empty container. Full product still arrives only from a completed REMOVE_FULL. | `store_flow_test.dart`, `FinishedGoodsAreasApiTests` | DROP-to-area mapping is not invented. |
| 14 Nineteen finished-goods areas exist | PASS | Migration `016_finished_goods_areas.sql` seeds `FG_AREA_01`–`FG_AREA_19`, enabled, capacity null. | `FinishedGoodsAreasApiTests` | None. |
| 15 Real 19 area names | CUSTOMER DATA REQUIRED | Display names are temporary `Alan 1`–`Alan 19`. Replay does not overwrite a renamed name. | `FinishedGoodsAreasApiTests` | TKG must supply the real area names. |
| 16 Warehouse request shows the requested Alt Part name | PASS | Warehouse demand includes part code and description, source machine, request number, preparation area, time, and readiness. | `WarehousePreparationApiTests` | None. |
| 17 Barcode | CUSTOMER DATA REQUIRED | No parser, scanner SDK, or barcode-to-part mapping was added. | None, by design | Scanner hardware, symbology, barcode-to-part source, wedge/camera/SDK choice, and the validation system. |
| 18 Semi-finished and finished-goods screens are separated by role | PASS | Login routes semi-finished operators to preparation and finished-goods operators to the store. A combined legacy warehouse role sees the warehouse home. | `WarehouseRoleAuthorizationApiTests`, `self_hosted_notifications_test.dart` | None. |
| 19 Finished-goods tablet is notified when a container is physically delivered | IMPLEMENTED — PHYSICAL DEVICE VALIDATION REQUIRED | The backend outbox and self-hosted notification API exist. Android foreground delivery exists. The outbox row `finished-goods-delivery:{storeUnitId}` is inserted in the same transaction as authoritative REMOVE_FULL completion, the new store unit, and UNIT_DELIVERED. Screen-off behavior still requires a physical tablet. Android Force Stop is not guaranteed. No Firebase. | `DeviceNotificationApiTests`, `FinishedGoodsNotificationCopyTests`, `self_hosted_notifications_test.dart` | Physical screen-off validation on a finished-goods tablet. |
| 20 The operator who receives full pallets can also leave empty pallets | PASS | Finished-goods and legacy warehouse operators can record an empty container on a vacant area. Semi-finished preparation stays on its own screen. | `store_flow_test.dart`, `DeviceNotificationApiTests` role boundary | None. |
| 21 All operators can view the global task list | PASS | Global task read remains available. | `GlobalTaskVisibilityAndCancellationScopeApiTests` | None. |
| 22 Cancellation is absent from the global list and allowed only on the machine screen | PASS | Global page has no cancel. Machine cancel stays machine-scoped. Warehouse roles do not gain machine cancel. | `GlobalTaskVisibilityAndCancellationScopeApiTests` | None. |
| 23 Operational and login audit information is persisted and queryable | PASS | Transport lifecycle events stay on `ops.transport_request_events`. Login and admin actions stay on `audit.audit_logs`. | `OperationalAuditEventsApiTests`, `OperationalAuditContainerActionEventsApiTests` | None. |
| 24 Total, warehouse response, and robot durations are separate | PASS | `GET /api/v1/admin/transport-requests/{id}/evidence`. Robot duration is null unless `ROBOT_TASK_STARTED` and `ROBOT_TASK_COMPLETED` both exist. `ROBOT_TASK_DISPATCHED` is not used as the start. | `TransportDurationMetricsTests` | None for the calculation. A live robot run was not required. |
| 25 Task, robot, and transport error history is queryable | PASS | Three distinct sources. Transport lifecycle and errors stay on `ops.transport_request_events`. Robot task and execution results stay on `integration.rds_tasks`, including the ended reason. Robot alarm observation history stays on `integration.robot_alarm_observations`. TKG stores observations of successful RDS current-alarm snapshots. This is not vendor-native RDS incident history. A failed RDS read does not clear an open observation. `RdsAlarmObservationWorker` observes on the server even when no Admin or PDA screen is open. | `RobotAlarmObservationApiTests`, `RdsAlarmObservationWorkerTests`, `OperationalEvidenceApiTests` | None for the observation model. |
| 26 The same sicil cannot be active on two field computers | PASS | One active field session. A second concurrent login is rejected. | `SingleActiveSessionApiTests` | None. |
| 27 Deprecated Admin screens stay removed | PASS | Navigation keeps dashboard, users, devices, machines, audit, robot integration, and the new priority page. Layout, catalog, container, and data-quality links stay out of the menu. | `AppShell.test.tsx` | None. |
| 28 M4 replacement of the RDS screen | VENDOR API REQUIRED | RDS remains the live execution layer. No M4 endpoint, auth, or task schema was added. | Existing RDS execution tests | M4 API documentation from the vendor. |
| 29 M4 park and charge tasks | VENDOR API REQUIRED | No park or charge API was invented. | None, by design | The same vendor contract, including park and charge operations. |
| 30 Incomplete "Robotun..." meeting note | CUSTOMER CONFIRMATION REQUIRED | The sentence was not completed and no behavior was guessed from it. | None, by design | TKG must confirm what the note was going to say. |

## Barcode information still required

- Scanner hardware and whether it is a keyboard wedge, camera, or vendor SDK.
- Barcode symbology and the exact payload format.
- Which system maps a barcode to a part.
- What the operator should do when the code is unknown or belongs to another machine.

## M4 information still required

- Base URL, authentication, and the task, status, park, and charge contracts.
- How an M4 task maps to the existing transport request and readiness gates.
