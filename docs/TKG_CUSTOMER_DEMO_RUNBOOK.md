# TKG production operator runbook

This is the operator path for the real-time production system.

```text
Flutter PDA / Web
→ ASP.NET Core
→ PostgreSQL
→ RDS execution layer
→ robot
```

RDS dispatch is a valid production capability. This runbook does not turn it off and does not make Commissioning read-only. A physical robot movement is an operational test decision, not a code limitation. Automated evidence below was executed against the local PostgreSQL API suite and the Flutter test suite. It is not a physical tablet test.

## Before the floor

1. Apply migrations through `020_robot_alarm_observation_state.sql`. The supported path is `tools/dev/start_local_stack.ps1`, which applies every file in `db/migrations` in filename order and then seeds `002a`, `002b`, and `002c`.
2. Leave RDS read integration enabled when the floor should observe robot alarms. `RdsAlarmObservationWorker` polls `GET /api/agv-report/core` on the server. The default interval is 10 seconds (`Rds:AlarmObservationIntervalSeconds`, allowed range 5–300). Dispatch stays available for valid queued work. Do not disable it merely to walk through this path.
3. Sign in a `LINE_OPERATOR` on a device that is already bound to one machine.
4. Sign in a semi-finished warehouse operator and a finished-goods operator on their own devices.
5. Do not enter production-end priority numbers. The admin page must show them empty until TKG supplies them.

## Operator path

1. The line operator logs in on the bound machine terminal. The server, not the tablet, decides the machine.
2. Active production shows the main product. That identity is the Mamul Kasa product.
3. Alt Parts are listed separately from the Mamul Kasa panel.
4. Tap one Alt Part. The request is created immediately.
5. The new request appears under Aktif Taşımalar, below Mamul Kasa, with the Alt Part code and name.
6. The operator was not asked for a container number, FULL, EMPTY, or a physical slot.
7. The semi-finished warehouse screen shows that same part code and description, the source machine, and the request number.
8. The warehouse operator prepares the pallet and marks it ready. The request stays out of robot dispatch until then.
9. Valid queued work can be sent to RDS. Status then moves QUEUED → SENT_TO_RDS → RUNNING → COMPLETED from the execution result. SENT_TO_RDS is not completion. No robot percentage is shown.
10. After the area is occupied by the same Alt Part, request that Alt Part again. `current_part_id` decides the physical pallet. FULL becomes REMOVE_FULL then BRING_FULL. EMPTY becomes REMOVE_EMPTY then BRING_FULL. A different or unknown pallet is not removed as though it were the requested part. A safe vacant mapped area receives a direct BRING_FULL.
11. The dependent BRING_FULL does not dispatch until the predecessor is COMPLETED and warehouse preparation is READY. SENT_TO_RDS is not completion. The card says the existing pallet is being removed or the previous task is still waiting.
12. Mamul Kasa stays on the main product while Alt Parts are selected.
13. Press Kasam Doldu. The server creates REMOVE_FULL from the authoritative load state. If the finished-goods destination is not mapped, the request is refused.
14. The REMOVE_FULL appears in the machine's active transports.
15. When the mamul location is vacant, Alan Boş creates BRING_EMPTY and waits for warehouse preparation. The machine is not marked occupied at creation.
16. Open Global Tasks from another operator. The fleet list is visible.
17. Global Tasks has no cancel action.
18. Cancel is available only on the authorized machine screen.
19. The finished-goods screen lists `FG_AREA_01` through `FG_AREA_19` as Alan 1–Alan 19. A vacant area is not shown as holding an empty container. Boş kasa bırak records one explicitly.
20. Admin audit shows login and configuration history. Transport evidence shows total, warehouse, and robot durations. Error history is three sources: `ops.transport_request_events`, `integration.rds_tasks`, and `integration.robot_alarm_observations`. Alarm history is TKG's observation of successful RDS snapshots, not a vendor incident log. A failed read does not clear an open alarm.
21. Semi-finished preparation and finished-goods store are different screens. The finished-goods tablet notification is created only after a full container is stored, with the text Kasa Koyuldu. Screen-off delivery still needs a physical tablet. Force Stop is not guaranteed.

## Executed evidence

- `dotnet build TKG.Pda.slnx --configuration Release`: succeeded, 0 warnings, 0 errors.
- Domain tests: 146 passed. Application tests: 318 passed. Auth tests: 15 passed.
- `tests/phase_03/run_postgres_integration.ps1`: 78 passed.
- `tests/phase_04/run_postgres_api_integration.ps1`: 244 passed. Migration replay digest stayed UNCHANGED. This includes part-demand ownership, finished-container actions, the 19 areas, self-hosted notification delivery, production-end priority configuration, and continuous alarm observation.
- `RdsAlarmObservationWorkerTests`: 8 passed, included in the phase 04 count. No live RDS connection was used.
- Flutter `flutter analyze`: no issues. `flutter test`: 251 passed.
- Admin Web source did not change, so its npm suite was not re-run.

## Not executed

- A physical Android tablet, including screen-off delivery. Force Stop is not guaranteed.
- Customer area names, barcode scanning, M4, park/charge, and production-end priority numbers.

RDS dispatch was not turned off. This automated run did not command a physical robot; that remains an operational choice when a named robot test is authorized.
