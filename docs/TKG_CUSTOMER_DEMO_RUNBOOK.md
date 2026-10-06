# TKG customer demo runbook

This is the operator path for the corrected workflow. Automated evidence below was executed against the local PostgreSQL API suite and the Flutter/Admin test suites. It is not a live robot run and not a physical tablet test.

## Before the room

1. Apply migrations through `018_production_end_priorities.sql`.
2. Leave RDS dispatch disabled unless the room has explicitly authorized a named robot test.
3. Sign in a `LINE_OPERATOR` on a device that is already bound to one machine.
4. Sign in a semi-finished warehouse operator and a finished-goods operator on their own devices.
5. Do not enter production-end priority numbers. The admin page must show them empty.

## Demo path

1. The line operator logs in on the bound machine terminal. The server, not the tablet, decides the machine.
2. Active production shows the main product. That identity is the Mamul Kasa product.
3. Alt Parts are listed separately from the Mamul Kasa panel.
4. Tap one Alt Part. The request is created immediately.
5. The new request appears under Aktif Taşımalar, below Mamul Kasa, with the Alt Part code and name.
6. The operator was not asked for a container number, FULL, EMPTY, or a physical slot.
7. The semi-finished warehouse screen shows that same part code and description, the source machine, and the request number.
8. The warehouse operator prepares the pallet and marks it ready. The request stays out of robot dispatch until then.
9. In the test environment, execution status moves QUEUED → SENT_TO_RDS → RUNNING → COMPLETED only through the existing simulator or test transition. SENT_TO_RDS is not completion. No robot percentage is shown.
10. After the area is occupied, request the same Alt Part again.
11. The result is one chain: remove the current pallet, then a dependent BRING_FULL. The second request does not dispatch until the first is COMPLETED and preparation is READY. The card says the existing pallet is being removed or the previous task is still waiting.
12. Mamul Kasa stays on the main product while Alt Parts are selected.
13. Press Kasam Doldu. The server creates REMOVE_FULL from the authoritative load state. If the finished-goods destination is not mapped, the request is refused.
14. The REMOVE_FULL appears in the machine's active transports.
15. When the mamul location is vacant, Alan Boş creates BRING_EMPTY and waits for warehouse preparation. The machine is not marked occupied at creation.
16. Open Global Tasks from another operator. The fleet list is visible.
17. Global Tasks has no cancel action.
18. Cancel is available only on the authorized machine screen.
19. The finished-goods screen lists `FG_AREA_01` through `FG_AREA_19` as Alan 1–Alan 19. A vacant area is not shown as holding an empty container. Boş kasa bırak records one explicitly.
20. Admin audit shows login and configuration history. Transport evidence shows total, warehouse, and robot durations, plus failed or cancelled events and the RDS task result when one exists.
21. Semi-finished preparation and finished-goods store are different screens. The finished-goods tablet notification is created only after a full container is stored, with the text Kasa Koyuldu.

## Executed evidence

- `tests/phase_04/run_postgres_api_integration.ps1`: 219 tests passed. Migration replay digest stayed UNCHANGED. This includes part-demand chains, finished-container actions, the 19 areas, self-hosted notification delivery, and production-end priority configuration.
- `TransportDurationMetricsTests`: total, warehouse, and robot durations, including a null robot duration when robot start is missing.
- `OperationalEvidenceApiTests`: priorities start empty, a saved value can be cleared, and an unconfigured operation fails with `PRODUCTION_END_PRIORITY_NOT_CONFIGURED`.
- Flutter `flutter analyze`: no issues. `flutter test`: 250 tests passed, including the alt-part demand widget and the machine-scoped cancel widget.
- Admin web `npm run test:run`: 49 tests passed. `npm run build` passed.

## Not executed

- A physical Android tablet, including screen-off delivery and Force Stop.
- A live SEER robot.
- Customer area names, barcode scanning, M4, park/charge, and production-end priority numbers.
