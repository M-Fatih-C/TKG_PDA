# TKG PDA — Local Acceptance Suite

This directory contains the canonical, repeatable, non-destructive acceptance test suite for TKG PDA. It validates the local development environment from a clean checkout against the persistent local development stack.

---

## 1. Prerequisites

Before running the acceptance suite, ensure the following tools are available on your system:

- **PowerShell Core (`pwsh`)**: Version 7.0+ recommended.
- **PostgreSQL 18 CLI (`psql`)**: In `PATH` or accessible locally.
- **Python 3**: With Selenium installed (`pip install selenium`).
- **Google Chrome**: Compatible with Selenium for the headless Operator Web E2E test.
- **.NET SDK & Flutter SDK**: For running unit/widget regression suites.

---

## 2. How to Start the Local Stack

The acceptance suite operates against the persistent local stack services:
- **PostgreSQL 18**: `127.0.0.1:55450` (`tkg_pda_dev`)
- **ASP.NET Core API**: `http://127.0.0.1:5000`
- **Admin Web**: `http://127.0.0.1:4173`
- **Operator Web**: `http://127.0.0.1:7357`

To start the local stack normally:

```powershell
pwsh -File tools/dev/start_local_stack.ps1
```

> **SAFETY NOTICE:**
> Never execute `start_local_stack.ps1` with `-ForceSeeds` or `-ResetSimulationState` during normal acceptance runs.
> The acceptance suite tests persistent state retention and non-destructive operations.

To stop the stack when finished:

```powershell
pwsh -File tools/dev/stop_local_stack.ps1
```

---

## 3. Canonical Execution Command

To run the complete acceptance suite:

```powershell
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1
```

### Optional Phase Execution

You can target specific phases using the `-Phase` parameter:

```powershell
# Run only Runtime Persistence across restart
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Persistence

# Run only 9-Machine Production matrix
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase NineMachine

# Run only Mamul Kasa state matrix
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase MamulKasa

# Run only Repeated ALT demand dependency tests
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Alt

# Run only Production End capacity and atomic rollback tests
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase ProductionEnd

# Run only real Operator Web Browser E2E test
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Browser

# Run only WorkSites static plan and live mirror report
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase WorkSites
```

### Flags

- `-SkipBrowser`: Skips the Selenium/Chrome browser automation phase (useful in CI environments lacking Chrome; note that skipping browser prevents the final status from reaching `LOCAL E2E PASS`).
- `-SkipRestart`: Skips the full stack restart during the persistence test.

---

## 4. Acceptance Test Structure

| Script | Purpose |
| :--- | :--- |
| `run_local_acceptance.ps1` | Canonical master orchestrator executing all phases in deterministic order. |
| `test_runtime_persistence.ps1` | Verifies that slot state and simulation buffer survive normal stack restart, and that `002d` overlay is strictly insert-missing. |
| `test_nine_machine_production.ps1` | Validates complete production lifecycle (start -> verify active session -> end) across all 9 active machines using dynamically resolved contexts. |
| `test_mamul_kasa.ps1` | Tests logical position action state matrix (`Kasa Doldu`, `Boş Kasa Getir`), button availability, and audits 100% absence of deprecated labels (`Alan Boş`, `Kasam Doldu`). |
| `test_repeated_alt_demand.ps1` | Tests real ALT demand dependency chains (`REMOVE` predecessor -> `BRING_FULL` successor), cancellation propagation, duplicate demand rejection, and missing container validation (25 blocked relations). |
| `test_production_end.ps1` | Tests production end destination routing (`FG-xx`, `RETURN-xx`, `EMPTY-xx`), idempotency, commissioning boundary error handling, and atomic rollback on capacity failure. |
| `test_operator_web_e2e.py` | Full end-to-end browser automation in Chrome against the real Operator Web app (`http://127.0.0.1:7357`): device setup, login, machine selection, main part selection, production start, Mamul Kasa verification, action triggering, and session cleanup. |
| `report_worksites.ps1` | Reports static RDS WorkSite plans (1775 invariant) and live mirrored `integration.rds_worksites` dynamic inventory. |

---

## 5. Non-Destructive Mutation & Cleanup Policy

The suite is safe against developer databases:

1. **Snapshots & Cleanup**:
   - Any test that alters operational state (e.g. `SLOT-001`, `BUFFER-001`, test production sessions) captures original state prior to testing.
   - All mutations are enclosed in `try ... finally` blocks ensuring exact restoration of occupancy, load, parts, container types, and row versions even on assertion failure.
2. **No Truncation or Dropping**:
   - Live tables, master data, customer layouts, and RDS worksites are never truncated or dropped.
3. **API First**:
   - Tests interact through public ASP.NET Core API endpoints wherever possible; direct SQL queries are used only for assertions or safe state restoration.

---

## 6. What PASS Means vs. FIELD READY

- **`LOCAL E2E PASS — FIELD COMMISSIONING INPUTS PENDING`**:
  All software business logic, API projections, database invariants, and real web user interactions pass completely against the persistent local development stack.
- **NEVER REPORT `FIELD READY`**:
  Field readiness requires on-site commissioning with physical robots and live floor validation. The software acceptance suite explicitly acknowledges the boundary between local software validation and physical commissioning.

---

## 7. Known External Field Commissioning Blockers

The following items are expected external boundaries pending field commissioning and are NOT software defects:

1. **DRAFT Navigation Profiles**: Customer navigation profiles remain in `DRAFT` status pending on-site physical commissioning with the live SEER AGV fleet.
2. **Physical Shared Endpoint Bindings**: Real physical bindings for `FG`, `RETURN`, and `EMPTY` profiles await field commissioning verification.
3. **Physical WorkSite Verification**: Live floor coordinate confirmation and physical tag placement.
4. **Robot Geometry & Calibration**: Live fork heights, pickup/drop geometry, and sensor recognition parameters.
5. **SEALER V3 Source Layout**: Marked `PENDING_SOURCE_LAYOUT` pending final customer engineering layout confirmation.
6. **Missing Container Metadata**: Exactly 25 ALT part relations lack container metadata in the customer master data and are correctly blocked with `422 CONTAINER_TYPE_MISSING`.
7. **Production End Priority Calibration**: Final customer operational priority values for production-end destinations.
