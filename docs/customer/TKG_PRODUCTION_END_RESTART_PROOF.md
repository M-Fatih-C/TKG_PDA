# Production End forensic and restart proof — 2026-10-09

## Classification and current state

**FAILED / RETRYABLE**, with origin **UNKNOWN** (HIGH confidence in an ordinary operator UI client; not proof of human/test ownership). The protected SH-01-A session `58797808-9b5b-4c99-83fc-72f21c023a96` is still ENDING with no end timestamp. Its single run `4036069c-65cb-4691-9eae-440297c4530d` is FAILED / PRODUCTION_END_CLEANUP_INCOMPLETE. Its single REMOVE_FULL child #143 was cancelled before dispatch. This is intentional failed-cleanup semantics, not an orphan or a completed run with an open session.

Four requests belong to the session: #140 BRING_EMPTY and #141–143 REMOVE_FULL, all currently CANCELLED. #141 and #142 previously reached SENT_TO_RDS and RUNNING, and carry STOPPED RDS records. No request has successful completion evidence. The End child #143 has no RDS task; it was queued at 09:29:18 and cancelled at 09:29:24+03:00. The full source pallet remains OCCUPIED/FULL. The run has one item, no dependency edge. Current runtime NAV is the pre-existing published DEV-RDS-SIM-MAP-A, not a published customer registry profile.

The start/End/container keys are ordinary PDA UUID keys, not owned acceptance identifiers. Container/cancel audit entries carry a normal Chrome user agent and localhost address. These support an interactive UI origin but cannot prove who controlled the browser. Historical RDS dispatch evidence prevents treating the session as an undispatched test artifact. Exact actor/device/row/event evidence is retained in the local forensic report; it is not duplicated as customer personal data in this source document.

## Lifecycle and reconciliation

`ProductionSessionEndpoints` → `ProductionSessionService.EndAsync` → `PostgresTransportWorkflowStore.EndAsync` locks the machine and validates authority. `PlanProductionEndAsync` snapshots/plans all removals, writes a RUNNING run and independent child requests/dependencies, and sets ENDING in the same transaction. An empty authoritative inventory completes and ends immediately.

`CancelAsync`, `RecordExecutionTransitionAsync`, and `RecordExecutionOutcomeAsync` update child state and call `AdvanceProductionEndRunAsync` **before the same transaction commits**. All successful children complete the run/session and produce one PRODUCTION_ENDED audit. FAILED/CANCELLED marks the run FAILED, cancels waiting successors, and preserves ENDING so an explicit new End key can re-plan current inventory. Same-key replay returns the original logical operation; it does not create a new plan. RUNNING runs or active transport block a new plan. The temporary ACTIVE state during re-planning is inside the machine-locked transaction.

No separate Production End startup scan exists. The hypothesized supported-path commit gap between child terminal status and run/session advancement does not exist here: those writes are atomic. A real API kill during advancement proves rollback. Current dev contains zero RUNNING runs with only terminal children. Manually/legacy-written inconsistent terminal rows are outside this proof; no broad cleaner or unsupported automatic transition was added.

RDS mirror persistence is a separate step: `ApplyTaskStatusAsync` records the RDS task snapshot, then calls the workflow. `ListActiveTasksAsync` selects by **transport** SENT_TO_RDS/RUNNING status, including an already-terminal mirrored RDS record. The existing RDS reconciliation worker can therefore retry after interruption when integration is enabled. RDS Off deliberately pauses external reconciliation. It is not permission to manufacture a completion.

## Confirmed software defect and minimal fix

`src/TkgPda.Pda/lib/app/app_controller.dart`, `canEndProduction`, rejected every ENDING session. That blocked the backend's supported fresh-key failed-run retry from the normal PDA. Existing tests checked the initial End response, not recovery after failed cleanup.

The PDA now permits an explicit End attempt when server state is verified, authority/mutation gates pass, and no active transport, pending/ambiguous submission or cart item exists. The backend retains its RUNNING-run lock/guard. The normal confirmation remains; both responsive panels tell the operator to verify field state before retry. `ProductionCoordinator` already clears the successful command key, so a new explicit attempt generates a fresh key; uncertain network retries still retain their existing key.

The idle-ENDING regression failed before the change and passes afterward. Active child transport still blocks retry without cancellation side effects. Widget coverage checks guidance and explicit confirmation. No backend lifecycle/schema change was justified.

## Real process fault injection (isolated phase-04 PostgreSQL only)

`RestartApiProcess` launches the built ASP.NET API in a real OS process on an ephemeral port. It refuses any database except the phase-04 throwaway database, refuses persistent API ports, sets all RDS/execution gates off, and always kills/disposes its owned process. Requests use normal passwordless employee/device authentication. No test adds a production diagnostic HTTP endpoint.

Six new DB-backed cases:

1. ALT REMOVE→BRING_FULL chain persists across API PID replacement. Warehouse readiness and predecessor dependency survive; matching retry retains the two request identities. Predecessor completion/replay yields exactly one successor eligibility and completion event.
2. Three-child End persists ENDING, identities and two dependencies across PID replacement. Children complete in order through the existing storage/domain transitions; only the last ends the session. A second restart preserves ENDED and one end audit.
3. Failed running child leaves FAILED/ENDING and cancels the waiting successor. After restart, same key adds no rows; a new End key creates a new run, preserves the old history and completes once.
4. Pre-execution cancellation exercises the same retry proof independently.
5. Final child completion commits before API kill/restart; durable run/session/audit/store records already converge and stay converged without callback replay.
6. An exact-run PostgreSQL trigger waits on a test-held advisory lock after the real API cancels a child, before run advancement. The test observes that wait, kills the API, releases the gate and verifies exact child/slot rollback, RUNNING run, ENDING session and no cancellation event. Restart and normal cancellation then mark the run FAILED consistently.

Terminal outcome simulation uses the existing workflow store in the isolated database, never a direct final-state UPDATE or live robot. The trigger injects an actual in-flight transaction interruption; it is removed in finally. The first targeted test run exposed an invalid test attempt to fail QUEUED directly; the fixture was corrected to follow SENT_TO_RDS→RUNNING→FAILED. Production rules were unchanged. Final targeted result: 6/6 PASS.

## Recovery boundary

The existing persistent session was not ended, deleted, reset, cancelled or rewritten. The current End run is already correctly FAILED. There is no deterministic lifecycle inconsistency to reconcile, and ownership is not proven test-specific.

`tools/dev/acceptance/inspect_production_end.ps1` defaults to, and only supports, read-only inspection:

```powershell
pwsh -File tools/dev/acceptance/inspect_production_end.ps1 -SessionId 58797808-9b5b-4c99-83fc-72f21c023a96
```

The SQL runs in REPEATABLE READ / READ ONLY and enumerates exact session/run/request identities, dependencies, events, RDS evidence, audit, layout runtime and referenced preparation/store records. There is deliberately no `-Apply` operation for unknown ownership or historical RDS execution. It neither deletes history nor calls API/RDS. Actor personal data and full evidence belong in the local report, not a committed static fixture.

Appropriate recovery is an authorized operator verifying current physical pallet state and explicitly issuing the existing End command with a **new** idempotency key. Backend preflight remains authoritative; missing/unknown state fails closed. Same-key replay only verifies the old run. No persistent recovery command was applied during this work.

## Browser target review

The browser test currently hardcodes SH-01-A/main part and uses its bound terminal/Mamul capabilities. Current read-only evidence: the other eight active machines are idle but have no active NAV and no EMPTY_STORAGE buffer in their active layout. SH-01-A has the published development NAV and buffer, but remains ENDING. An equivalent source-backed/terminal-authorizable Mamul smoke target was not established. No device was rebound, machine silently skipped or synthetic workflow relabelled as persistent browser proof. Nine-machine acceptance stays explicit and environment-blocked.

## Regression and acceptance

Starting local/remote SHA: `304582710afd617d24619c184109de528d0445be`. Final SHA/remote equality and clean-tree evidence are in the delivered local report and Git history. Verified results: Domain 146, Application 327, Auth 15, PostgreSQL 83, Flutter 299, Admin 66, non-live API 555 and browser assertion units 8: **1499 distinct cases, zero failures/skips**. Flutter analysis, Admin lint/typecheck/build and nine safety checks pass. Live RDS tests remain explicitly excluded; this is not a live motion result.

Canonical acceptance: **10 PASS, 4 BLOCKED, 1 SKIP; exit 2; 459.1s**. Nine-machine, Mamul, persistent ALT and persistent End are ENVIRONMENT_BUSY; browser is SKIP. Isolated workflows/sentinel, 555/555 API, restart snapshots and original-row preservation pass. A separate after-forensics comparison verifies the protected session/run/items/requests/dependencies/events/RDS/audit/slots/runtime/preparation/store evidence is identical to the initial snapshot. The phase-04 shutdown-wait warning is separate from successful assertions; no phase-04 listener remains. None of the mandatory assertions or busy-state guards was weakened.

## Preserved backlog and final status boundary

A09 Admin commissioning diagnostic; A20 M4 active controls/park-charge authoring; native tablet/background notification hardware validation; real NAV publication, FG/RETURN/EMPTY binding, geometry/calibration, SEALER V3, 25 missing ALT metadata rows, approved customer priorities and barcode research remain open.

Final status: **CODE PASS — LOCAL E2E ENVIRONMENT BLOCKED**. FIELD READY is not a possible outcome of this task.
