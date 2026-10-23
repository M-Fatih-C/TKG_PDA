# TKG post-acceptance audit — 2026-10-09

## Initial finding inventory (before implementation)

Baseline branch: `agent/tkg-rds-map-worksite-registry`. Local and remote SHA: `b3775e14445abe89bd03100a8cdb92d64a76d3ed`. Initial working tree clean.

Customer source: `TKG_Toplanti_Notlari_02_10_2026.xlsx`, both worksheets, feedback rows 8–36 and screen explanations. The workbook has five columns; its introductory reference to column I is stale, and original wording is actually in column E. Current task instructions supersede historical Mamul action labels.

| ID | Severity | Ownership | Confidence | Status | Finding and minimal response |
| --- | --- | --- | --- | --- | --- |
| A01 | P1 | SOFTWARE | CONFIRMED | FIX NOW | `ValidateProductionActorAsync` in `PostgresTransportWorkflowStore.ProductionSessions.cs` requires a legacy user-machine permission even for an authorized bound line terminal. Context-only terminal tests miss production start/end. Recheck terminal binding and explicit legacy restrictions at the storage boundary; exercise start/end without a permission row. |
| A02 | P1 | SOFTWARE | HIGH | FIX NOW | `CreatePartDemandWithinTransactionAsync` takes the production-session row before generic creation takes the machine row. Production End takes machine then session. This inversion can deadlock concurrent ALT and End. Serialize both single and batch ALT on the machine before other workflow locks; add a PostgreSQL concurrency regression. |
| A03 | P2 | SOFTWARE | CONFIRMED | FIX NOW | `TransportEtaService.Project` manufactures AVAILABLE ETA from a constant 120-second cycle and queue position without RDS evidence. Existing tests require the fabricated value. Return UNAVAILABLE without valid remaining-time telemetry; test missing/invalid telemetry. |
| A04 | P1 | TEST_INFRASTRUCTURE | CONFIRMED | FIX NOW | `test_runtime_persistence.ps1` restores the buffer version to zero, loses original slot version/timestamps and uses opaque customer UUIDs. Sentinel does not run persistence or snapshot its buffer. Preserve full runtime rows, resolve identities by relationships, reject busy targets before mutation, and compare all persistent runtime rows. |
| A05 | P1 | TEST_INFRASTRUCTURE | CONFIRMED | FIX NOW | `test_safety_sentinel.ps1` inserts a fabricated shared endpoint binding in persistent dev, contrary to the commissioning boundary. Run destructive sentinel setup only in acceptance DB, and independently compare existing persistent business rows around Tier 1. |
| A06 | P1 | TEST_INFRASTRUCTURE | CONFIRMED | FIX NOW | Browser E2E catches an undefined `m_id`/unauthenticated projection fetch, compares button counts instead of disabled state, unconditionally passes badge honesty, and treats an empty active-section heading as a transport. It sets PASS without checking all verifications. Enforce actual authenticated projection, per-position action state and actual request identity; fail on any unmet assertion. Track browser-created session ownership explicitly. |
| A07 | P2 | TEST_INFRASTRUCTURE | CONFIRMED | FIX NOW | Persistent Production End accepts any error as DRAFT rejection. A 401/500 can pass. Assert HTTP status and exact problem code; remove restoration writes to untouched slots. |
| A08 | P2 | TEST_INFRASTRUCTURE | CONFIRMED | FIX NOW | Canonical runner claims LOCAL E2E PASS for a partial phase or skipped restart/synthetic tier. It also starts/restarts with default dispatch-enabled commissioning mode. Require all mandatory phases for that claim and start acceptance services with RDS Off. |
| A09 | P3 | UX/OPERABILITY | CONFIRMED | BACKLOG | Admin readiness exposes NAV/geometry/endpoint blockers but not a complete container-metadata and customer-approved priority diagnostic. Preserve scope; document the SQL/report dependency. |
| A10 | P2 | CUSTOMER_DATA | CONFIRMED | EXTERNAL | Missing ALT container metadata, SEALER V3 layout, FG display names and final priorities require authoritative customer inputs. No fabricated values. |
| A11 | P1 | FIELD_COMMISSIONING | CONFIRMED | EXTERNAL | Real NAV publication, geometry and profile-specific FG/RETURN/EMPTY physical bindings require field evidence. Local synthetic evidence cannot establish FIELD READY. |
| A12 | P1 | SOFTWARE | CONFIRMED | FIX NOW | Three template selectors order nullable equality DESC, which sorts generic NULL ahead of a matching TRUE override in PostgreSQL. `GetDispatchContextAsync`, `ResolveFinishedGoodsDestinationSlotAsync`, `ListConfiguredReturnDestinationsAsync` can choose wrong labels/destinations. Add NULLS LAST and coexistence/fallback regression coverage. Existing fixtures predominantly contain only one applicable template. |

No commits made during the initial audit. Final evidence, coverage, readiness and counts will be appended after verification.

Additional source-traced P2 SOFTWARE / CONFIRMED / FIX NOW findings:

- A13: `_isConfigurationIssue` classifies `BUSINESS_RULE_NOT_CONFIGURED` (unknown runtime state) as missing configuration by suffix. Use the explicit configuration-code set; regression expects `Hazır Değil` and disabled actions for unknown runtime state.
- A14: `ApiProblem.operatorMessage` collapses nine actionable container, capacity and WorkSite errors into generic 422 text. Map only these source-traced errors; keep unknown/internal errors generic and retain backend status/code.
- A15: P2 TEST_INFRASTRUCTURE / CONFIRMED / FIX NOW. Phase 03 omits migrations 011–013; isolated acceptance leaves its line terminal unbound and selects DRAFT navigation as active. Apply the existing SQL migrations and bootstrap only published synthetic navigation/terminal binding. A clean database exposed errors hidden by persistent developer setup.
- A16: P1 SOFTWARE / CONFIRMED / FIX NOW. `IsFinishedGoodsDestinationConfiguredAsync` checks only legacy delivery slots; a commissioned registry FG binding can accept a direct FULL command while its PDA projection disables FULL. Use the active profile's healthy FG binding and enabled canonical store location for registry projection, with no legacy fallback. Regression covers healthy, disabled, unsynced and missing binding.
- A17: P1 TEST_INFRASTRUCTURE / CONFIRMED / FIX NOW. Normal `start_local_stack.ps1` deletes historical requests/parts/slots/users by test-name prefixes and one hardcoded fixture identity. A startup does not own these records. Remove implicit cleanup, retain all existing records, and cover preservation with complete row snapshots.
- A18: P2 TEST_INFRASTRUCTURE / CONFIRMED / FIX NOW. Normal replay of `002d` refreshes `last_sync_at` for ten existing deterministic simulation WorkSites despite no successful live read. Full mirror snapshots detected this during restart. Make those mirror inserts insert-only; verify all existing mirror rows across two normal startups. No SQL migration changed.
- A19: P2 TEST_INFRASTRUCTURE / CONFIRMED / FIX NOW. Four tests excluded from the former phase-04 filter expect warehouse machine-context access or a template-free seeded variant. Full non-live API execution exposed them. Keep the established role boundary: assert HTTP 403 `ROLE_NOT_AUTHORIZED` for all warehouse roles, still verify safe JSON for allowed PDA roles, and explicitly set up absent template evidence in the isolated fixture.
- A20: P2 UX/OPERABILITY / CONFIRMED / BACKLOG. Meeting notes G13 require M4 active-task controls and park/charge task creation. `AppShell.tsx` and `RdsIntegrationPage.tsx` retain the RDS name and a monitoring-only screen; no park/charge authoring exists in the clients/application. Discovery of park points is not task execution. Implementing this needs a separately reviewed application workflow and source-backed robot/target mappings; it is outside a minimal defect patch and was not added.


## Final audit evidence

### A. Repository

Repository: `M-Fatih-C/TKG_PDA`; branch: `agent/tkg-rds-map-worksite-registry`; starting local/remote SHA: `b3775e14445abe89bd03100a8cdb92d64a76d3ed`. The initial inventory above was written before implementation or commits. Changes are split into software/regression and acceptance-infrastructure/report commits. The final response and Git history provide the final commit SHA, remote equality and Draft PR; embedding a commit's own SHA inside that commit is not possible. No SQL migration or EF migration was added/changed. No live RDS endpoint or robot motion was used.

### B. Findings and disposition

20 confirmed findings: P0 **0**, P1 **9**, P2 **10**, P3 **1**. Ownership: SOFTWARE **7**, TEST_INFRASTRUCTURE **9**, CUSTOMER_DATA **1**, FIELD_COMMISSIONING **1**, UX/OPERABILITY **2**. **16 fixed**, **2 external**, **2 backlog** (A09, A20). A02 is now CONFIRMED by the real PostgreSQL lock-overlap regression. A14 grew from the initially demonstrated nine missing messages to 51 source-traced actionable messages; no HTTP/problem-code contract changed.

The scope remains the existing workflow. A09 is a commissioning diagnostic gap; A20 is a larger unimplemented meeting-note M4 control workflow. Neither justified adding a dashboard or unsafe robot command interface in this patch.

### C. Confirmed software defects and regressions

Infrastructure file paths below are under `src/TkgPda.Infrastructure.Postgres/`; API tests under `tests/TkgPda.Api.Tests/`.

| Finding | Exact component / failure / why old coverage missed it | Minimal fix | Regression evidence |
| --- | --- | --- | --- |
| A01 P1 | `PostgresTransportWorkflowStore.ProductionSessions.cs`, `ValidateProductionActorAsync`: a LINE_OPERATOR with a valid terminal binding but no legacy permission row passes context authorization and fails production start/end. Old terminal tests checked context only. | Revalidate active terminal→machine at storage boundary; preserve explicit legacy denial, supervisor permission and admin behavior. | `ProductionSessionApiTests.Bound_line_terminal_can_start_and_end_without_legacy_user_machine_permissions`; `Storage_boundary_rejects_line_terminal_rebound_to_another_machine`. |
| A02 P1 | `PostgresTransportWorkflowStore.PartDemand.cs`, single/batch entry; generic and container entry methods in `PostgresTransportWorkflowStore.cs` and `.ContainerActions.cs`: ALT acquires session before machine while End acquires machine before session. Sequential tests cannot reveal inversion. | Acquire machine runtime lock before workflow/idempotency/session locks at these entry points. ALT's early optional configuration check preserves existing replay error semantics. | `PartDemandApiTests.Alt_demand_waits_for_machine_before_locking_the_production_session` (single + batch): hold machine in another PG transaction, verify actual blocked demand, then acquire session NOWAIT. Old order fails 55P03. Concurrent distinct-key ALT clicks yield one create + one conflict. |
| A03 P2 | `src/TkgPda.Application/ContainerActions.cs`, `TransportEtaService.Project`: constant cycle/queue arithmetic reported available ETA without RDS timing; tests encoded that assumption. | Return UNAVAILABLE without finite valid remaining-time evidence. | `TransportEtaServiceTests`: missing data, negative/non-numeric/overflow telemetry, valid zero and positive remaining time. |
| A12 P1 | `PostgresRdsExecutionStore.cs/GetDispatchContextAsync`; `.FinishedGoodsStore.cs/ResolveFinishedGoodsDestinationSlotAsync`; `.ProductionEnd.cs/ListConfiguredReturnDestinationsAsync`: PostgreSQL DESC puts generic NULL before matching TRUE. Fixtures with only one applicable template missed wrong overrides. | Add `NULLS LAST` to all three selectors. No label fallback or customer label invented. | `VariantScopedRdsCommissioningApiTests.Dispatch_prefers_active_product_group_override_and_falls_back_to_generic_when_inactive`; `ProductionEndCleanupApiTests.Product_specific_return_group_wins_over_a_generic_destination_group`. |
| A13 P2 | `src/TkgPda.Pda/lib/features/operation/container_position_panel.dart`, `_isConfigurationIssue`: broad suffix matching labels unknown runtime state as missing configuration. | Explicit configuration-code membership only. | `container_position_actions_test.dart`: `BUSINESS_RULE_NOT_CONFIGURED`, two visible disabled actions, Hazır Değil, no green Hazır/configuration-missing badge. |
| A14 P2 | `src/TkgPda.Pda/lib/core/errors/api_problem.dart`, `operatorMessage`: actionable storage/container/workflow codes collapse into generic 422 text. Existing tests checked only a small subset. | Add 51 precise source-backed operator messages; retain generic handling for internal/invalid-client cases, ignore unsafe server detail. | `api_problem_presentation_test.dart`, literal message table and unknown/internal detail protection. Code inventory in J. |
| A16 P1 | `.FinishedGoodsStore.cs/IsFinishedGoodsDestinationConfiguredAsync`: projection checks legacy slots although actual registry FULL creation uses canonical FG binding; commissioned direct command succeeds while button is disabled. Old fixtures include both paths. | For active registry NAV, use published profile + healthy same-profile binding + enabled canonical store location; keep legacy path for non-registry context. | `SharedEndpointReservationApiTests.Mamul_projection_uses_healthy_registry_fg_binding_without_legacy_delivery_slots`: healthy enables FULL and persists FG-01; disabled/unsynced/missing bindings disable FULL. |

A08 additionally verifies the managed API process (including dotnet child ownership) and recorded RDS-Off mode before selected/SkipRestart probes; unverifiable state bootstraps safely with RDS Off. Existing running databases are snapshotted before bootstrap, cold starts after PostgreSQL becomes available.

Test infrastructure changes A04–A08/A15/A17–A19 are in `tools/dev/acceptance/`, `tools/dev/start_local_stack.ps1`, phase-03 test bootstrap, existing simulation seed and role-contract API fixtures. A19 also restores all original template fields in a `finally` block: the first expanded rerun exposed seed loss (92 instead of 96) caused by test setup, not production data. The full suite was rerun after repair.

### D. Reviewed suspicious patterns that are accepted

- Deterministic UUIDs in SQL seeds and isolated test fixtures are stable identities. Customer contexts in persistent probes resolve business codes and relationships. No opaque customer UUID was added to Flutter production code. Business codes FG/RETURN/EMPTY are intentional contracts.
- Occupancy is VACANT/OCCUPIED/UNKNOWN; load is EMPTY/FULL/UNKNOWN. Reservations are workflow records, not an occupancy enum. Product Group remains below Variant. `master.layout_slot_parts` drives routing; extraction metadata is provenance. Machine display regions and EMPTY_STORAGE are not machine-side part endpoints.
- Mamul EMPTY is BRING_EMPTY. REMOVE_EMPTY remains internal cleanup vocabulary; direct operator selection is forbidden. Mamul FULL is REMOVE_FULL. Fixed direct actions bypass an extra confirmation; unknown states disable actions.
- BRING_FULL waits for independent warehouse readiness, then for any required predecessor. Batch children retain independent request/idempotency/RDS identities and one active machine variant.
- EMPTY_SOURCE is not a single-capacity destination reservation. FG, RETURN and EMPTY_RETURN use canonical destination locks across NAV profiles. Terminal FAILED/CANCELLED/COMPLETED releases transport reservation; store inventory is tracked separately. These tests do not certify physical capacity/sensors.
- 96 generic templates = 24 source-backed variants × four operations. SEALER V3 has no source-backed layout; its absent execution templates are intentional. Registry config uses `RDSCORE_NATIVE_ORDER` and `tkg_variant_task_templates_v1`.
- Dynamic mirrors and MAP_SWITCH_AREA discovery are observations, not `1888` or `100` invariants. PARK/CHARGE nodes are not material endpoints. No rows were deleted to force counts.
- All customer registry profiles remain DRAFT. One pre-existing SH-01-A V1 `geometryVerified=true` flag is reported as stored data, not accepted field evidence. It was not set by this audit; DRAFT and absent bindings still block dispatch.
- Legacy username/password/device and production employeeNumber/device coexist; Admin username/password has no mandatory PDA binding. Single active field login is separate from a production session.
- RDS alarms remain read-only snapshot evidence from `GET /api/agv-report/core`; failed read does not clear observed history and telemetry does not alter load/occupancy. Mock RDS response tests are adapter/contract tests, not live robot evidence.

### E. Requirement-to-code coverage

Paths abbreviated below: **D** = `src/TkgPda.Domain/`; **A** = `src/TkgPda.Application/`; **API** = `src/TkgPda.Api/`; **PG** = `src/TkgPda.Infrastructure.Postgres/`; **PDA** = `src/TkgPda.Pda/lib/`; **Admin** = `src/TkgPda.AdminWeb/src/`. PG workflow partial filenames are prefixed `PostgresTransportWorkflowStore.`. Test class names identify files under API/Domain/Application test projects, or PDA test filenames. No Admin surface is required for every operator rule; a dash means not applicable, not missing implementation.

| # / customer rule | Domain/application | API | DB/storage | PDA | Admin | Automated evidence | Local acceptance / field dependency | Classification |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 ALT click creates demand | D PartDemandRules; A PartDemand | ApiEndpoints part-demand routes | PG PartDemand; requests | app/app_controller, operation part flow | — | PartDemandApiTests; part-demand PDA tests | Synthetic ALT PASS; physical mapping pending | IMPLEMENTED + TESTED |
| 2 repeated REMOVE→BRING | D PartDemandRules; A PartDemand | part-demand single/batch | PG PartDemand; dependency rows | part flow / active transport | — | PartDemandApiTests, Domain PartDemandRulesTests | Synthetic multi-cycle PASS; no process-kill-between-chain test | IMPLEMENTED + TESTED (restart limit below) |
| 3 finished-only Mamul | A ContainerActions | ApiEndpoints container positions/actions | PG ContainerActions, FinishedGoodsStore | container_position_panel | — | ContainerActionsApiTests; widget matrix | Persistent phase busy; isolated/API covered | IMPLEMENTED + TESTED |
| 4 fixed actions no extra confirm | A container action service | direct action routes | PG ContainerActions | app_controller/direct buttons | — | container_position_actions_test, ContainerActionsApiTests | Browser currently SKIP | IMPLEMENTED + TESTED |
| 5 line vs warehouse | A warehouse/container policies | ApiSecurity, WarehousePreparationEndpoints | terminal/role checks | warehouse/operation role routing | users/devices | WarehouseRoleAuthorizationApiTests | Isolated/API PASS | IMPLEMENTED + TESTED |
| 6 semi/finished warehouse split | A WarehousePreparation, FinishedGoodsStore | warehouse/store endpoints | PG WarehousePreparation, FinishedGoodsStore | warehouse_role_split_flow | users | WarehouseRoleAuthorizationApiTests; warehouse_role_split_flow_test | Physical store targets pending | IMPLEMENTED + TESTED |
| 7 terminal→machine | A production/workflow boundary | ApiSecurity, production endpoints | audit.client_devices; PG ProductionSessions | terminal binding flow | devices | TerminalMachineBindingApiTests; new A01 regressions | API PASS; actual site terminal enrollment pending | IMPLEMENTED + TESTED |
| 8 machine active progress | A ContainerActions/RdsExecution | operational-context | PG request projection, rds_tasks | machine_home / active transport | Reports/RDS | OperatorInformationBoundaryApiTests; PDA progress tests | ETA unavailable without telemetry; browser SKIP | IMPLEMENTED + TESTED |
| 9 End priority table | A ProductionSessions | ProductionSessionEndpoints; Admin priority routes | PG ProductionEnd; master.production_end_priorities | production_session_panel | ProductionEndPriorityPage | ProductionEndCleanupApiTests | Synthetic PASS; final values not approved | IMPLEMENTED + TESTED / EXTERNAL INPUT REQUIRED |
| 10 19 FG logical areas | A FinishedGoodsStore | FinishedGoodsStoreEndpoints | ops.store_locations FG-01..19 | warehouse/store_screen | Reports | FinishedGoodsStoreApi/PG tests | 19 observed; names pending | IMPLEMENTED + TESTED / EXTERNAL INPUT REQUIRED |
| 11 part/sub-part name | A queue/preparation projections | operational context + queue | joins to master.parts | queue, active transport, preparation | Reports | OperatorInformationBoundaryApiTests, WarehousePreparationApiTests | Synthetic/API PASS | IMPLEMENTED + TESTED |
| 12 barcode research | No source-backed matching contract | — | no invented association | no claimed matching workflow | — | No implementation claim | Customer feasibility/input pending | EXTERNAL INPUT REQUIRED |
| 13 both labels always | A two-action projection | positions route | PG ContainerActions | container_position_panel | — | widget matrix, API availability | Kasa Doldu / Boş Kasa Getir; browser SKIP | IMPLEMENTED + TESTED |
| 14 FULL=REMOVE_FULL | A ContainerActionResolutionPolicy | FULL command | PG ContainerActions | direct-action-REMOVE_FULL | — | ContainerActionsApiTests; widget C | API PASS; FG field bindings pending | IMPLEMENTED + TESTED |
| 15 EMPTY=BRING_EMPTY | A ContainerActionResolutionPolicy | EMPTY command | PG ContainerActions | direct-action-BRING_EMPTY | — | ContainerActionsApiTests, widget A/B | API PASS; empty-source field mapping pending | IMPLEMENTED + TESTED |
| 16 active disables both | A availability | positions route | active reservation query | two disabled buttons | — | ContainerActionsApiTests; widget E | Persistent phase busy; unit/API verified | IMPLEMENTED + TESTED |
| 17 honest readiness badge | A availability | disabledCode/status | PG configuration/availability | explicit _isConfigurationIssue | readiness | A13 widget regression, browser assertion units | Browser actual flow SKIP | IMPLEMENTED + TESTED |
| 18 End FG/RETURN/EMPTY | A ProductionSessions | ProductionSessionEndpoints | PG ProductionEnd/SharedEndpoints | end production panel | priorities/readiness | ProductionEndCleanupApiTests, PresentationExecutionAlignmentApiTests | Synthetic PASS; customer physical bindings pending | IMPLEMENTED + TESTED |
| 19 canonical endpoint codes | D FinishedGoodsDestinationContract | store/End/request routes | PG SharedEndpoints; destination_business_code | friendly business labels | readiness/RDS | SharedEndpointReservationApiTests | FG01..19 RETURN/EMPTY01..08 contract; no fake bindings | IMPLEMENTED + TESTED |
| 20 per-NAV WorkSite | A RdsExecution | execution/commissioning routes | PostgresRdsExecutionStore, profile_shared_endpoint_bindings | distilled readiness only | RdsIntegration/CommissioningReadinessPage | VariantScopedRdsCommissioningApiTests, SharedEndpointReservationApiTests | Dispatch fails closed on absent physical evidence | IMPLEMENTED + TESTED |
| 21 cross-NAV destination capacity | A allocation/workflow | requests/End | PG SharedEndpoints canonical lock | disabled/capacity messages | readiness | actual concurrent SharedEndpointReservationApiTests | Synthetic physical stand-ins only | IMPLEMENTED + TESTED |
| 22 SEALER V3 unresolved | A commissioning projections | Admin commissioning API | source registry PENDING_SOURCE_LAYOUT | unavailable context | CommissioningReadinessPage | PresentationExecutionAlignmentApiTests | 1 profile pending source; no invented layout | EXTERNAL INPUT REQUIRED |
| 23 missing ALT container blocked | D PartDemandRules | part-options/demand | product_group_parts container_type_id | non-requestable option / message | incomplete diagnostic (A09) | PartDemandApiTests | Persistent read-only blocker PASS, 25 rows | IMPLEMENTED + TESTED / EXTERNAL INPUT REQUIRED |
| 24 real commissioning external | A RdsExecution gates | RDS mutation/readiness policy | registry + geometry + real binding | honest blocker | CommissioningReadiness/RdsIntegrationPage | RdsDispatchConfigurationTests, commissioning API tests | RDS Off; no field execution | EXTERNAL INPUT REQUIRED |

Additional meeting-note traceability (all 29 feedbacks, workbook rows 8–36):

| Feedback numbers / screen | Evidence and remaining gap | Classification |
| --- | --- | --- |
| 1–2 G01 | Requirements 1–2 above | IMPLEMENTED + TESTED |
| 3–5 G02 | Requirements 3–4, 13–17; historical labels superseded by current request | IMPLEMENTED + TESTED |
| 6–7 G03 | Requirements 5–7 | IMPLEMENTED + TESTED |
| 8–11 G04/G05 | Active transport below Mamul requests in machine home, names/states and progress; queue is refreshed, unavailable ETA remains unknown | IMPLEMENTED + TESTED; current Chrome interaction not re-proven |
| 12 G06 | Priority configuration implemented; customer approval absent | IMPLEMENTED + TESTED / EXTERNAL INPUT REQUIRED |
| 13–15 G07 | Finished warehouse store occupancy/actions and 19 areas; display names pending | IMPLEMENTED + TESTED / EXTERNAL INPUT REQUIRED |
| 16–17 G08 | Preparation part name implemented; barcode matching remains research | IMPLEMENTED + TESTED / EXTERNAL INPUT REQUIRED |
| 18 G09 | Semi/finished role screens, preparation vs store rights | IMPLEMENTED + TESTED |
| 19 G09 | DeviceNotificationEndpoints + PostgresDeviceNotificationStore; warehouse notification coordinator/store delivery events; DeviceNotificationApiTests and warehouse_notification_flow_test. Native/background tablet delivery not exercised on hardware; web native-notification stub explicitly unsupported | IMPLEMENTED + WEAKLY TESTED for actual tablet delivery |
| 20 G09 | Finished warehouse role covers taking full and placing empty inventory through store actions | IMPLEMENTED + TESTED |
| 21–22 G10 | Global queue read surface; no cancellation in operator_queue_screen; selected machine cancellation remains server-authorized. Operator queue/authority tests cover boundary | IMPLEMENTED + TESTED |
| 23–25 G11 | OperationalReporting + PostgresOperationalReportStore, audit/login events and warehouse/robot timestamps; report and audit tests. RDS alarm observations are snapshots, not an incident-history promise | IMPLEMENTED + TESTED for recorded fields; real robot timing awaits field input |
| 26 G11 | Single active field session; SingleActiveSessionApiTests, Auth tests and single_active_session_test | IMPLEMENTED + TESTED |
| 27 G12 | AppShell excludes layout/parts/container/data-quality menu links; route test coverage | IMPLEMENTED + TESTED |
| 28–29 G13 | Monitoring/telemetry exists; M4 rename and active controls/park/charge task UI absent (A20). Park-point discovery does not fulfill robot task creation | PARTIAL; separate scoped implementation required |

The `Ekran Görselleri` worksheet's screen explanations were read with the feedback sheet. Embedded historical screenshots are reference context, not evidence of today's running browser.

### F. Persistent DB safety

Two normal startup cycles with RDS Off compare full snapshots of 14 tables, including every pre-existing session/request, all runtime versions/timestamps, mirrors/mappings, NAV profiles, priorities, bindings and client devices. The current original session `58797808-9b5b-4c99-83fc-72f21c023a96` on SH-01-A remains ENDING, `ended_at=NULL`, with its original start key. No test owns it; no End/DELETE was issued against it. Other machine probes that need an idle SH-01-A target return ENVIRONMENT_BUSY before mutation.

Normal startup's historical prefix/UUID cleanup was removed (A17). The earlier diagnostic restart exposed ten existing **simulation** mirror timestamps being refreshed by seed replay (A18); this was an actual original harness defect, not hidden by restoration. Existing mirror inserts now do nothing on conflict; subsequent full mirror snapshots pass. No customer profile was published or binding/geometry changed.

Safety-pattern classification:

| Match/context | Classification | Reason |
| --- | --- | --- |
| `audit_readonly.sql`, snapshots, worksite reports | SAFE-PERSISTENT | SELECT-only evidence; checked SQL failures cannot become PASS. |
| Guarded Mamul temporary slot updates + `Restore-RuntimeRow` | SAFE-PERSISTENT | Idle target checked before write; full original row restored in finally. Busy target never touched. |
| Exact generated request/session cleanup in persistent tests/browser | SAFE-PERSISTENT | Captured IDs/start key/request identities only; browser refuses unknown requests. No machine-wide deletion. |
| Acceptance DB drop/create; isolated sentinel/geometry/bindings/completion | SAFE-ISOLATED | Explicit database and API gates precede SQL/HTTP mutation. |
| Throwaway API/PG ResetAsync cleanup | SAFE-ISOLATED | Harness-owned cluster/database, not developer data. |
| Old runtime reset-to-zero, dev binding sentinel, implicit startup cleanup | UNSAFE → FIXED A04/A05/A17 | Removed/replaced; original defects documented. |
| Geometry/publication and ForceSeeds/ResetSimulationState references outside normal runner | NEEDS-REVIEW if invoked | Explicit commissioning/destructive maintenance scope only; not exercised by this audit. Literal presence alone is not a defect. |

Sentinel now runs in acceptance DB; original developer rows are independently checked around Tier 1 and at final runner completion. Gates: **9/9 PASS** (five database refusals, three persistent API port refusals, one normal-startup cleanup policy). No bad-row repair or operational deletion was performed.

### G. Isolated synthetic acceptance

`init_acceptance_db.ps1` uses SQL migrations and deterministic seeds; no operational data is copied from dev. It only selects a published synthetic NAV, binds the isolated line terminal, and leaves customer registry profiles DRAFT. Dedicated API 5001 is stopped in finally. Sentinel, synthetic Production End and repeated ALT use exact owned identities or isolated reset/setup; the sentinel cannot run against dev.

Production End verifies finished/non-finished/empty classification, deterministic children, dependencies, replay and capacity rollback. Synthetic ALT verifies repeated cycles, predecessor ordering/outcomes and rejection. These are local synthetic application/DB workflows, not RDS motion tests.

### H. Concurrency and restart limits

| Actual concurrent case | Evidence / expected result |
| --- | --- |
| Two Kasa Doldu commands on one position | `ContainerActionsApiTests.Concurrent_container_actions_create_only_one_reservation`: Task.WhenAll, one Created, one Conflict, one request. |
| Two Boş Kasa Getir / BRING_EMPTY requests | `PostgresTransportWorkflowTests.Concurrent_requests_for_the_same_slot_create_only_one_reservation`: one success/one SLOT_ALREADY_RESERVED; same-key concurrent replay yields one logical row. |
| Two ALT clicks | New PartDemandApiTests distinct-key tasks: one Created/one PART_REQUEST_ALREADY_ACTIVE, one request. |
| ALT vs Production End lock order | New single/batch PG lock-overlap tests prove machine precedes session; NOWAIT session lock remains available while ALT waits for machine. |
| Two Production End calls | ProductionSessionApiTests concurrent six End calls: one end event/audit; concurrent idempotent retries. This case uses vacant cleanup inventory; occupied multi-child planning is covered separately, not claimed as an occupied End race. |
| Cross-NAV FG | SharedEndpointReservationApiTests concurrent tasks: distinct free FG endpoints or exact capacity rejection when one remains. FAILED/CANCELLED/COMPLETED reuse verified. |
| Cross-NAV RETURN / EMPTY_RETURN | Actual concurrent commissioning-binding fixtures reject second reservation on one canonical destination. WorkSite resolution remains profile-specific. |
| Variant switch vs active transport / production start | ProductionSessionApiTests machine-lock overlap and active-transport rejection tests. |
| Cancel vs dispatch | PG `Concurrent_cancellation_and_dispatch_do_not_deadlock`; API execution/cancel guards and callback transition tests. No live callback server involved. |
| Start vs demand and stale End replay | ProductionSessionApiTests actual overlapping tasks, old-session replay cannot end a new session. |

Runtime/idempotency records and dependency rows survive normal startup; persisted replay and predecessor FAILED/CANCELLED/COMPLETED are covered by DB-backed tests. A process crash/restart specifically between ALT REMOVE and BRING, and during occupied multi-child Production End, was **not** induced. This is IMPLEMENTED + WEAKLY TESTED for that failure injection, not a fabricated restart E2E result. Sequential and concurrent coverage are distinguished. A redundant shared-endpoint same-machine test has a weaker count assertion; the exact one-row assertion above supplies the critical proof.

### I. Read-only database integrity

`tools/dev/acceptance/audit_readonly.sql` observes: active machines **9**, variants **25**, source-backed **24**, registry NAV **25**, active generic templates **96**, FG internal areas **19**, WorkSite plans **1775**, layout-slot plan bindings **208**, missing ALT containers **25**. No count was forced by deleting rows.

| Query | Invalid/collision count |
| --- | --- |
| Orphan dependency predecessors/successors | 0 |
| Request missing task type/machine | 0 |
| Session missing machine | 0 |
| Multiple active sessions per machine | 0 |
| Orphan slot runtime | 0 |
| Cross-machine source/destination/layout | 0 |
| Binding / mirrored WorkSite NAV mismatch | 0 |
| Duplicate active canonical destination allocation | 0 |
| Invalid active variant/layout relation | 0 |
| Unknown destination business code | 0 |
| Broken product-group/part references | 0 |
| Duplicate active templates | 0 |
| Source-backed variant missing required operation | 0 |

Dynamic mirrored WorkSites observed **3763**: DEV-RDS-SIM **1875**, DEV-RDS-SIM-MAP-A **1885**, TKG_SH01A_V01_NAV **3**. MAP_SWITCH_AREA: **2** mirrored profiles, **200** points. Mirror minus plans is 1988, not an unexplained invariant violation: mirrors include simulation/transit discovery and need not equal plans or all registry NAV counts. Three registry mirror rows do not establish field health/bindings.

### J. Error presentation inventory

Source enumeration finds **131 literal WorkflowRuleException codes**, **85** mapped in Flutter, **46** retaining generic/status-based presentation. The scan is reproducible from literal constructors; it is not an exhaustive claim about dynamically supplied adapter/authentication codes. **51** precise actionable operator mappings were added. Backend HTTP status and problem code were preserved. Raw detail/JWT/RDS identifiers are not exposed to operators.

Remaining 46 codes concern malformed client input, internal transitions/locks, warehouse/admin contracts or generic not-found/conflict outcomes; generic display is acceptable for the normal guided machine flow. Their literal index and newly mapped codes are appended below. None of these 46 was proven to need a new precise normal-operator message in this audit. Future reachable UI paths should reassess that classification.

### K. Customer demo path today

Status describes the real customer database, not a synthetic profile. LOCAL DEMO READY means software behavior was verified; it does not remove the recorded existing-session blocker.

| Step | Status | Exact consequence today |
| --- | --- | --- |
| Login | LOCAL DEMO READY | Employee number + enrolled device; single active login enforced. |
| Terminal-bound machine | LOCAL DEMO READY | Bound line terminal restricts machine; cannot operate another machine or warehouse route. |
| Main part | LOCAL DEMO READY | Source-backed master parts/relationships projected. |
| Variant | LOCAL DEMO READY for 24; CUSTOMER DATA BLOCKED for SEALER V3 | Active production/transport prevents switch; V3 lacks source layout. |
| Üretime Başla | FIELD BLOCKED; local SH-01-A environment also busy | Customer NAVs DRAFT; SH-01-A already ENDING. Isolated published synthetic start passes. |
| ALT part | CUSTOMER DATA BLOCKED for 25 relations; FIELD BLOCKED for dispatch | Missing container is non-requestable; valid synthetic demand and repeated chain pass. |
| Warehouse preparation | LOCAL DEMO READY for API/UI workflow; FIELD BLOCKED for robot source | Correct warehouse role marks readiness; BRING_FULL does not dispatch before ready. |
| Active transport | LOCAL DEMO READY for scoped projection | Queued/active requests with names/states; current Chrome new-request flow SKIP due original session. |
| Progress | LOCAL DEMO READY for honest state projection; FIELD BLOCKED for actual robot telemetry | No fabricated ETA; unknown telemetry remains unavailable. |
| Mamul Kasa | LOCAL DEMO READY for tested rendering | Every position displays both actions; disabled/blocker status is honest. |
| Kasa Doldu | FIELD BLOCKED | REMOVE_FULL requires real FG profile mapping; healthy synthetic binding proves projection/create alignment. |
| Boş Kasa Getir | FIELD BLOCKED | BRING_EMPTY requires real EMPTY_SOURCE/pickup mapping, not REMOVE_EMPTY. |
| Üretimi Bitir | FIELD BLOCKED / CUSTOMER DATA BLOCKED | FG/RETURN/EMPTY geometry/bindings and approved priorities absent; existing SH-01-A session never ended for demo. |

No evaluated regression remains SOFTWARE BLOCKED. Current browser re-proof is environment-blocked and prevents a new LOCAL E2E PASS. Meeting-note M4 park/charge demo is separately SOFTWARE BLOCKED (A20) and field-target input pending.

### L. Nine-machine field checklist

All registry NAV publish states remain **DRAFT / BLOCKED**. All real registry FG/RETURN/EMPTY binding counts are **0/0/0 / BLOCKED**. Final Production End priority approval is **PENDING INPUT** on every machine (stored development REMOVE_FULL=40, REMOVE_EMPTY=70; not customer-approved). Layout names are scoped to each machine. WorkSite plans are planned records, not live sites.

| Machine | Source-backed variants | Exact NAV profile(s) | Layout | Publish | Geometry | FG / RETURN / EMPTY bindings | Planned / mirrored sites | Missing ALT containers | End priorities | Overall field |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 412B | V1, V2, V3 | TKG_412B_V01_NAV<br>TKG_412B_V02_NAV<br>TKG_412B_V03_NAV | CUSTOMER-PHYSICAL-V1<br>CUSTOMER-PHYSICAL-V2<br>CUSTOMER-PHYSICAL-V3 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 213 / 0 (observed) | PENDING INPUT: 3 | PENDING INPUT | BLOCKED |
| GAR | V1, V2, V3 | TKG_GAR_V01_NAV<br>TKG_GAR_V02_NAV<br>TKG_GAR_V03_NAV | CUSTOMER-PHYSICAL-V1<br>CUSTOMER-PHYSICAL-V2<br>CUSTOMER-PHYSICAL-V3 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 213 / 0 (observed) | PENDING INPUT: 4 | PENDING INPUT | BLOCKED |
| PAH-01-A | V1, V2 | TKG_PAH01A_V01_NAV<br>TKG_PAH01A_V02_NAV | CUSTOMER-PHYSICAL-V1<br>CUSTOMER-PHYSICAL-V2 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 142 / 0 (observed) | PENDING INPUT: 6 | PENDING INPUT | BLOCKED |
| PAH-02-A | V1, V2 | TKG_PAH02A_V01_NAV<br>TKG_PAH02A_V02_NAV | CUSTOMER-PHYSICAL-V1<br>CUSTOMER-PHYSICAL-V2 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 142 / 0 (observed) | PENDING INPUT: 8 | PENDING INPUT | BLOCKED |
| SEALER | V1, V2; V3 PENDING INPUT | TKG_SEALER_V01_NAV<br>TKG_SEALER_V02_NAV<br>TKG_SEALER_V03_NAV | CUSTOMER-PHYSICAL-SHARED-V1-V2<br>V3 PENDING_SOURCE_LAYOUT | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 213 / 0 (observed) | READY: none missing | PENDING INPUT | BLOCKED |
| SH-01-A | V1, V2, V3, V4, V5 | TKG_SH01A_V01_NAV<br>TKG_SH01A_V02_NAV<br>TKG_SH01A_V03_NAV<br>TKG_SH01A_V04_NAV<br>TKG_SH01A_V05_NAV | CUSTOMER-PHYSICAL-V1<br>CUSTOMER-PHYSICAL-V2<br>CUSTOMER-PHYSICAL-V3<br>CUSTOMER-PHYSICAL-V4<br>CUSTOMER-PHYSICAL-V5 | BLOCKED (DRAFT) | BLOCKED: V1 stored true, no field evidence; V2–5 false | BLOCKED: 0 / 0 / 0 | 355 / 3 (observed) | READY: none missing | PENDING INPUT | BLOCKED |
| SH-01-B | V1, V2, V3 | TKG_SH01B_V01_NAV<br>TKG_SH01B_V02_NAV<br>TKG_SH01B_V03_NAV | CUSTOMER-PHYSICAL-V1<br>CUSTOMER-PHYSICAL-V2<br>CUSTOMER-PHYSICAL-V3 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 213 / 0 (observed) | READY: none missing | PENDING INPUT | BLOCKED |
| SH-01-P1--SH-01-P2 | V1, V2 | TKG_SH01P1P2_V01_NAV<br>TKG_SH01P1P2_V02_NAV | CUSTOMER-PHYSICAL-SHARED-V1-V2 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 142 / 0 (observed) | PENDING INPUT: 2 | PENDING INPUT | BLOCKED |
| SH-02-P1 -SH-02-P2 | V1, V2 | TKG_SH02P1P2_V01_NAV<br>TKG_SH02P1P2_V02_NAV | CUSTOMER-PHYSICAL-SHARED-V1-V2 | BLOCKED (DRAFT) | BLOCKED (false) | BLOCKED: 0 / 0 / 0 | 142 / 0 (observed) | PENDING INPUT: 2 | PENDING INPUT | BLOCKED |

SEALER has 213 plans including 71 provisional plan identities for V3; these do not constitute a supplied physical layout. SH-01-A's three mirrored rows and stored V1 geometry flag are insufficient for commissioned dispatch. All plans use naming-registry identities; no production task label, fork height, robot or geometry was inferred.

### M. Regression results

| Suite / command | Pass | Fail | Skip | Evidence |
| --- | ---: | ---: | ---: | --- |
| Domain `dotnet test tests/TkgPda.Domain.Tests` | 146 | 0 | 0 | `scratch/domain-tests.log`, TRX |
| Application `dotnet test tests/TkgPda.Application.Tests` | 327 | 0 | 0 | `scratch/application-tests.log`, TRX |
| Auth `dotnet test tests/TkgPda.Auth.Tests -c Release` | 15 | 0 | 0 | `scratch/auth-tests.log`, TRX |
| Flutter `flutter test` | 296 | 0 | 0 | `scratch/flutter-tests-result.log` |
| Admin `npm test` | 66 | 0 | 0 | 13 files, `scratch/admin-tests.log` |
| PostgreSQL phase 03 integration | 83 | 0 | 0 | `scratch/postgres-verified.log`; existing migrations 011–013 now applied |
| All non-live API, phase 04 | 549 | 0 | 0 | `scratch/acceptance-final.log`; explicit LiveRdsPostgresSyncTests exclusion |
| Browser assertion units | 8 | 0 | 0 | `python tools/dev/acceptance/test_browser_assertions.py` |
| **Distinct automated cases** | **1490** | **0** | **0** | Excluded live-RDS tests are not counted as executed/skipped cases. |

Additional checks: Flutter analyze **PASS / no issues**; Admin lint **PASS**; Admin `tsc -b && vite build` **PASS** (includes typecheck); safety gates **9/9 PASS**. The focused eight-case Mamul widget rerun after removing unrelated formatting and strengthening disabled-button assertions also passes; it is not double-counted.

Canonical `pwsh -File tools/dev/acceptance/run_local_acceptance.ps1`: **10 PASS, 4 BLOCKED, 1 SKIP**, declared exit **2**, elapsed **435.9s**. Git baseline, preflight, restart persistence, WorkSite report, original-row preservation, synthetic DB gate, isolated sentinel, synthetic Production End, synthetic ALT, and 549/549 API checks pass. Nine-machine/Mamul/persistent ALT/persistent End are ENVIRONMENT_BUSY; browser is SKIP. The original ENDING session is the exact blocker. A later focused browser run after the artifact-output repair again returns exit 2 and writes a fresh SKIP report, replacing the stale historical PASS artifact. The final runner mode guard and observed-transit wording were exercised with a targeted WorkSites rerun; selected-check CODE PASS and original-row preservation were verified again.

The phase-04 harness prints `pg_ctl` shutdown wait failure **after** successful 549 tests and verification JSON PASS. No phase-04 listener remains on 55439; this teardown warning is separate from assertion counts. The acceptance runner still reports preserved original rows. Initial failed runs are retained in scratch logs: stale warehouse/template assertions, test-seed restoration issue, and an overlapping-build CS2012. The final full run supersedes them; no production rule was relaxed to satisfy tests. Scratch logs are local ignored artifacts and contain no committed credentials.

### N. Remaining inputs and deferred work

- Customer: authoritative SEALER V3 physical layout; container type/lot evidence for 25 blocked ALT relations; names for 19 FG areas; approved Production End priority values; barcode matching feasibility and authoritative source contract.
- Field: actual machine/variant NAV publication, geometry/calibration evidence, robot/task labels/fork heights/pickup-drop geometry as required, canonical FG/RETURN/EMPTY bindings per NAV, successful live mirror health and explicitly authorized commissioning tests.
- Local re-proof: an authorized idle demo target/environment to execute corrected Chrome and persistent workflow probes. Existing production sessions must not be sacrificed for this.
- A09: a separately scoped Admin diagnostic improvement for complete ALT metadata and priority approval readiness.
- A20: meeting-note M4 active controls and park/charge workflow; needs reviewed scope, application boundary and source-backed robot/target mapping. No live motion was attempted.
- Actual native tablet notification/background delivery and fault-injection restart between ALT chain steps / occupied End remain unverified on hardware/process crash.

### O. Final status

**CODE PASS.** The prior LOCAL E2E PASS is withdrawn because A06/A08 proved acceptance correctness defects: the old browser could pass with empty active transport/unconditional badge checks and skipped/partial mandatory runs could be called full E2E. This is not a downgrade caused by intentionally absent field data. Evaluated software and isolated workflows now pass, original developer rows survive, but the corrected browser/full persistent flow cannot be re-proven while the original SH-01-A session is ENDING. No FIELD READY claim is supported.

### Error-code appendix

New precise Flutter operator messages (51 source-traced literal codes):

`ACTIVE_LAYOUT_REQUIRED`, `BUSINESS_RULE_NOT_CONFIGURED`, `CANCELLATION_NOT_ALLOWED`, `CONTAINER_POSITION_NOT_FOUND`, `CONTAINER_POSITION_OCCUPIED`, `CONTAINER_STATE_CONFLICT`, `EMPTY_BUFFER_SLOT_NOT_CONFIGURED`, `EMPTY_BUFFER_STATE_MISMATCH`, `EMPTY_PALLET_HAS_MATERIAL`, `EMPTY_RETURN_CAPACITY_UNAVAILABLE`, `FILLED_SUPPLY_POSITION_NOT_VACANT`, `FILLED_SUPPLY_POSITION_RESERVED`, `FINISHED_GOODS_CAPACITY_UNAVAILABLE`, `IDEMPOTENCY_KEY_REUSED`, `NON_FINISHED_RETURN_CAPACITY_UNAVAILABLE`, `OCCUPIED_CONTAINER_REQUIRED`, `PART_AREA_AMBIGUOUS`, `PART_AREA_CURRENT_PART_UNKNOWN`, `PART_AREA_OCCUPIED_BY_OTHER_PART`, `PART_AREA_STATE_CONFLICT`, `PART_DEMAND_DUPLICATE`, `PART_SLOT_NOT_CONFIGURED`, `PREPARATION_AREA_ALLOCATED`, `PREPARATION_AREA_NOT_VACANT`, `PREPARATION_CONTAINER_MISMATCH`, `PREPARATION_DEMAND_ALREADY_CLAIMED`, `PREPARATION_DEMAND_INCOMPLETE`, `PREPARATION_DEMAND_REQUIRED`, `PREPARATION_LOAD_MISMATCH`, `PREPARATION_LOAD_UNKNOWN`, `PREPARATION_PALLET_CHANGED`, `PREPARATION_PALLET_NOT_FOUND`, `PREPARATION_PALLET_NOT_PLACED`, `PREPARATION_PALLET_NOT_READY`, `PREPARATION_PALLET_REQUIRED`, `PREPARATION_PART_MISMATCH`, `PREPARATION_READINESS_CONFLICT`, `PREPARATION_REQUEST_NOT_WAITING`, `PREPARATION_VERSION_CONFLICT`, `PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE`, `PRODUCTION_SESSION_NOT_FOUND`, `RDS_WORKSITE_DISABLED`, `RDS_WORKSITE_SYNC_FAILED`, `REMOVE_ALL_NOTHING_TO_REMOVE`, `REMOVE_ALL_NOT_READY`, `REMOVE_ALL_POSITION_CONFLICT`, `REMOVE_ALL_POSITION_UNKNOWN`, `REQUEST_TERMINAL`, `SHARED_ENDPOINT_CAPACITY_UNAVAILABLE`, `SLOT_STATE_MISMATCH`, `STORE_VERSION_CONFLICT`

Remaining generic/status-based presentation (46 literal domain codes):

`BATCH_IDEMPOTENCY_KEY_REQUIRED`, `BATCH_IDEMPOTENCY_KEY_TOO_LONG`, `BATCH_ITEMS_REQUIRED`, `BATCH_ITEM_IDEMPOTENCY_KEY_DUPLICATED`, `BATCH_MACHINE_MISMATCH`, `CONTAINER_ACTION_INVALID`, `CONTAINER_BATCH_DUPLICATE_POSITION`, `CONTAINER_BATCH_ITEM_INVALID`, `CONTAINER_CONFLICT`, `CONTAINER_POSITION_INVALID`, `DESTINATION_REQUIRED`, `EMPTY_PREPARATION_NOT_REQUEST_BOUND`, `EMPTY_STORAGE_MACHINE_SLOT_FORBIDDEN`, `IDEMPOTENCY_KEY_REQUIRED`, `IDEMPOTENCY_KEY_TOO_LONG`, `ID_REQUIRED`, `ILLEGAL_STATUS_TRANSITION`, `INVALID_PRIORITY`, `MACHINE_REGION_FORBIDDEN`, `PART_PRODUCT_GROUP_CONFLICT`, `PART_REQUIRED_FOR_AUTOMATIC_SLOT_RESOLUTION`, `PART_VARIANT_CONFLICT`, `PREPARATION_ACCESS_DENIED`, `PREPARATION_AREA_INVALID`, `PREPARATION_CONFLICT`, `PREPARATION_IDENTITY_REQUIRED`, `PREPARATION_PART_REQUIRED`, `PREPARATION_RELEASE_REASON_INVALID`, `PREPARATION_ROW_VERSION_REQUIRED`, `PRODUCT_GROUP_REQUIRED`, `QUEUE_PAGE_INVALID`, `READINESS_NOT_APPLICABLE`, `REQUEST_ID_REQUIRED`, `REQUEST_LOCK_SCOPE_CHANGED`, `REQUEST_NOT_FOUND`, `SAME_SOURCE_DESTINATION`, `SLOT_NOT_FOUND`, `SOURCE_REQUIRED`, `STORE_ACCESS_DENIED`, `STORE_IDENTITY_REQUIRED`, `STORE_LOCATION_NOT_FOUND`, `STORE_ROW_VERSION_REQUIRED`, `STORE_UNIT_NOT_FOUND`, `TASK_TYPE_NOT_FOUND`, `UNSUPPORTED_EXECUTION_OUTCOME`, `UNSUPPORTED_EXECUTION_UPDATE`
