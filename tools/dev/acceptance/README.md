# TKG PDA — Local Acceptance Suite

The runner separates persistent development evidence from isolated synthetic workflows. A passing assertion suite does not establish physical commissioning readiness.

## Database boundaries

| Tier | Target | Policy |
| --- | --- | --- |
| 1: Persistent evidence | `tkg_pda_dev`, PostgreSQL 55450, API 5000 | Read-only restart snapshots and guarded customer projections. Workflow probes refuse pre-existing active sessions/requests. Temporary slot changes restore exact values, timestamps and versions. Cleanup uses exact test-owned identities. |
| 2: Synthetic workflows | `tkg_pda_acceptance`, PostgreSQL 55450, dedicated API 5001 | Recreated from SQL migrations and deterministic seeds; no operational data copied from dev. Synthetic publication, geometry, bindings and completion are permitted here only. |
| 3: DB-backed API regression | Phase 04 throwaway PostgreSQL cluster | Entire API suite except `LiveRdsPostgresSyncTests`. No connection to persistent dev and no live RDS test. |

All runner startup/restart calls specify `-RdsMode Off`. Targeted runs and `-SkipRestart` verify the managed stack mode and API process ownership; unverifiable or dispatch-enabled state triggers RDS-Off bootstrap before probes. Persistent API URLs are restricted to the managed local endpoint. A cold PostgreSQL bootstrap establishes its snapshot once the database is available; existing running databases are snapshotted before startup. Normal startup has no implicit cleanup of historical test-name rows. Existing simulated WorkSite mirror rows retain their timestamps when seeds replay.

## Run

```powershell
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1
```

Useful targeted phases:

```powershell
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Sentinel
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase NineMachine
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase ProductionEnd
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase SyntheticProductionEnd
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase SyntheticAlt
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Browser
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase ApiUnit
```

`ApiUnit` is a historical phase name: it runs DB-backed API integration, not only unit mocks. `Sentinel` belongs to Tier 2 and initializes its isolated database/API.

Flags: `-SkipBrowser`, `-SkipRestart`, `-SkipSynthetic`, and `-DropAcceptanceDb`. A partial run or a skipped mandatory phase cannot earn `LOCAL E2E PASS`.

Prerequisites: PowerShell 7, PostgreSQL 18 CLI, .NET, Flutter, Python with Selenium, and Chrome. Passwordless dev identities use employee numbers. Optional password authentication reads `TKG_TEST_USERNAME`, `TKG_TEST_PASSWORD`, and `TKG_TEST_EMPLOYEE_NUMBER` from the environment; never print these values or pass passwords to startup as literals.

## Checks

| Script | Evidence and limit |
| --- | --- |
| `persistent_safety.ps1` | Checked SQL and full-row snapshots across 14 business/configuration tables. Every pre-existing row must remain byte-equivalent as JSON, including versions/timestamps. Test-owned closed sessions may be appended. |
| `test_runtime_persistence.ps1` | Read-only snapshots across two normal RDS-Off startups; no injected slot/buffer state, hardcoded customer UUID, or restoration mutation. |
| `test_nine_machine_production.ps1` | Customer production boundary checks; returns `ENVIRONMENT_BUSY` before operating a busy target. Only exact owned session IDs may be cleaned up. |
| `test_mamul_kasa.ps1` | Two visible actions, FULL→REMOVE_FULL, EMPTY→BRING_EMPTY, active-request disabling, exact temporary state restoration on idle targets. |
| `test_production_end.ps1` | Persistent DRAFT rejection must be HTTP 409 `NAVIGATION_PROFILE_CONFLICT`; unrelated 401/500 cannot pass. Never publishes customer profiles. |
| `test_repeated_alt_demand.ps1` | Customer missing-container projection and exact `CONTAINER_TYPE_MISSING` rejection; no resulting request. |
| `test_operator_web_e2e.py` | Actual Chrome flow checks authenticated projection, per-card enabled states, honest badge and actual newly created request identities. Every verification must pass. Captures this browser's session/start key before cleanup; refuses unknown requests. Busy existing production safely returns SKIP. |
| `report_worksites.ps1` | Static plan count plus observed mirror/transit breakdown. Dynamic mirrored inventory is never a fixed equality invariant. |
| `test_safety_sentinel.ps1` | Isolated sentinel entities, including synthetic binding; checks preservation across guarded phases. Never injects sentinel commissioning data into dev. |
| `init_acceptance_db.ps1` | Explicit acceptance-DB gate before destructive setup; applies existing SQL migrations. Binds the synthetic line terminal and selects published synthetic NAV only. |
| `test_synthetic_production_end.ps1` | Isolated cleanup planning/execution, child requests, dependency ordering, replay and exact capacity rejection/atomicity. |
| `test_synthetic_repeated_alt.ps1` | Isolated multi-cycle demand, REMOVE→BRING dependency, predecessor outcomes, replay and demand rejection. |
| `test_acceptance_safety.ps1` | Five database refusal checks, three persistent API-port refusal checks and startup cleanup-policy check. |
| `test_browser_assertions.py` | Eight pure assertion regressions. These are unit tests of the browser harness, not browser E2E. |
| `audit_readonly.sql` | Read-only inventory, machine commissioning rows and integrity/collision queries. Reports evidence; performs no repair. |

## Safety and result semantics

Gates run before SQL/HTTP mutations. Synthetic scripts reject any database other than `tkg_pda_acceptance` and reject persistent API port 5000. Persistent targets with pre-existing active sessions or requests return exit 2 before temporary state changes. Existing sessions must never be ended to make the suite pass.

Persistent cleanup is restricted to captured test-owned request/session identities; `DELETE ... WHERE machine_id = ...` is not allowed. Customer NAV status, geometry and shared bindings are never fabricated. `ForceSeeds` and `ResetSimulationState` are not used by normal acceptance.

The persistent snapshots include sessions, requests, dependencies, slot/machine runtime, store units, shared bindings, mirrored WorkSites, legacy mappings, templates, store locations, NAV profiles, priorities and client devices. Two normal startups compare whole snapshots; the overall runner verifies every original full row survives.

| Result | Meaning |
| --- | --- |
| `LOCAL E2E PASS`, exit 0 | Full `All` run, no mandatory skips/blockers, successful browser and isolated workflows, API regressions and persistent preservation. |
| `CODE PASS`, selected checks, exit 0 | Selected checks passed; full local E2E was not evaluated. |
| `CODE PASS`, blocked/skipped, exit 2 | Evaluated checks passed, original rows preserved, but pre-existing activity or explicit skips prevented full acceptance. |
| Safety/test failure, exit 1 | A required evaluated assertion failed. Never reinterpret an authentication/server error as an expected commissioning blocker. |

`FIELD READY` additionally requires source-backed customer layout/metadata, approved priorities, published real NAV, physical geometry/calibration and real profile-specific FG/RETURN/EMPTY WorkSite bindings. A simulated completion or a stored geometry flag alone does not establish that evidence. PARK/CHARGE must never be substituted for material-flow endpoints.

The 2026-10-09 audit, meeting-note traceability, exact test counts and external blockers are recorded in `docs/customer/TKG_POST_ACCEPTANCE_AUDIT.md`.
