# TKG PDA — Local Acceptance Suite

This directory contains the hardened, repeatable acceptance test suite for TKG PDA. It enforces a strict architectural boundary between **persistent development safety** and **isolated synthetic execution**.

---

## 1. Dual-Tier Database Architecture

The acceptance suite explicitly separates testing into two tiers to prevent data corruption, session destruction, or commissioning boundary violations:

| Tier | Target Database | Description & Policy |
| :--- | :--- | :--- |
| **Tier 1: Persistent Safety Acceptance** | `tkg_pda_dev` (Port 55450) | **READ-MOSTLY / STRICTLY NON-DESTRUCTIVE**.<br>- Validates invariants, baseline metrics, API projections, and browser flow.<br>- **NEVER** ends pre-existing active sessions.<br>- **NEVER** deletes rows by `machine_id`.<br>- **NEVER** mutates customer navigation profiles to `PUBLISHED`.<br>- **NEVER** injects fake/trial shared endpoint bindings.<br>- Exact state snapshots are restored in `finally`. |
| **Tier 2: Isolated Synthetic Acceptance** | `tkg_pda_acceptance` (Port 55450) | **DEDICATED ISOLATED DATABASE**.<br>- Recreated automatically with clean schema and deterministic customer/dev seeds.<br>- Operates against a dedicated secondary API instance (`http://127.0.0.1:5001`).<br>- Allows published synthetic NAV, verified geometry, synthetic RDS worksites, and destructive multi-cycle execution.<br>- **Strict Safety Gate:** Refuses to run if target database is `tkg_pda_dev` or any non-acceptance database.<br>- Dropped or preserved via runner flags. |
| **Tier 3: API test suite** | Throwaway phase 04 cluster | The repository phase 04 runner creates its own database. It never connects to `tkg_pda_dev`. A harness `pg_ctl` cleanup warning after a recorded PASS is reported separately. |

---

## 2. Prerequisites & Credentials

- **PowerShell Core (`pwsh`)**: Version 7.0+ recommended.
- **PostgreSQL 18 CLI (`psql`)**: Port 55450.
- **Python 3 with Selenium**: For Operator Web browser E2E.
- **Google Chrome**: For headless Selenium automation.
- **Zero Hardcoded Passwords**: Operator and supervisor tests use passwordless dev identity (`employeeNumber` `DEV-LINE` or `DEV-SUP`). Password authentication, when required, reads `TKG_TEST_USERNAME`, `TKG_TEST_PASSWORD`, and `TKG_TEST_EMPLOYEE_NUMBER`. The runner never prints those values and never passes a password literal to stack startup.

---

## 3. Canonical Execution Command

To run the complete hardened acceptance suite:

```powershell
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1
```

### Targeted Phase Execution

```powershell
# Run safety regression sentinel test
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Sentinel

# Run 9-machine production lifecycle (pre-existing session safe)
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase NineMachine

# Run persistent Production End commissioning blocker test
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase ProductionEnd

# Run synthetic full-e2e Production End in isolated acceptance DB
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase SyntheticProductionEnd

# Run synthetic Repeated ALT in isolated acceptance DB
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase SyntheticAlt

# Run real Operator Web Browser E2E test
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Browser

# Run API Unit & Mock tests
pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase ApiUnit
```

### Flags

- `-SkipBrowser`: Skips Chrome browser automation (leaves browser status as SKIP).
- `-SkipRestart`: Skips stack restart during persistence testing.
- `-SkipSynthetic`: Skips Tier 2 isolated synthetic execution.
- `-DropAcceptanceDb`: Drops `tkg_pda_acceptance` after synthetic acceptance completes.

---

## 4. Test Suite Mapping

| Script | Tier | Purpose |
| :--- | :--- | :--- |
| `run_local_acceptance.ps1` | Orchestrator | Master runner orchestrating Tier 1, Tier 2, and Tier 3 with safety assertions. |
| `test_runtime_persistence.ps1` | Tier 1 (Persistent) | Verifies slot and buffer state persistence across stack restart. |
| `test_nine_machine_production.ps1` | Tier 1 (Persistent) | Validates 9-machine start/end lifecycle. **Rejects pre-existing sessions with `ENVIRONMENT_BUSY`**; cleans up only test-owned IDs. |
| `test_mamul_kasa.ps1` | Tier 1 (Persistent) | Tests container action matrix (`Kasa Doldu`, `Boş Kasa Getir`); exact snapshot restore. |
| `test_production_end.ps1` | Tier 1 (Persistent) | Verifies customer NAV profile is DRAFT, production start is blocked by DRAFT status, planning rules for FG/RETURN/EMPTY, and unbound Production End blocker. Never modifies customer profiles. |
| `test_repeated_alt_demand.ps1` | Tier 1 (Persistent) | Validates the ALT relationship, the requestable projection, and the current count of container-missing relations (25). A direct demand is rejected with `CONTAINER_TYPE_MISSING` and creates no transport. |
| `test_operator_web_e2e.py` | Tier 1 (Persistent) | Real Chrome E2E automation. Detects pre-existing active session on `SH-01-A` and skips safely with `ENVIRONMENT_BUSY`; owns and cleans up only its created session ID. |
| `report_worksites.ps1` | Tier 1 (Persistent) | Audits static RDS WorkSite plans (1775) and live mirrored worksite inventory. |
| `test_safety_sentinel.ps1` | Tier 1 (Persistent) | **Safety Regression Guard:** Seeds 5 pre-existing sentinel entities (active session, request, shared binding, DRAFT profile, slot state) and proves byte-for-business-state preservation across acceptance runs. |
| `init_acceptance_db.ps1` | Tier 2 Tooling | Recreates isolated `tkg_pda_acceptance`, applies migrations 001..026 and customer seeds. Safety gated against `tkg_pda_dev`. |
| `test_synthetic_production_end.ps1` | Tier 2 (Isolated) | Full synthetic workflow: published NAV, trial bindings, sequenced child requests, dependency chaining, idempotent replay, and capacity failure atomic rollback in `tkg_pda_acceptance`. |
| `test_synthetic_repeated_alt.ps1` | Tier 2 (Isolated) | Full synthetic multi-cycle ALT demand workflow, dependency chaining, and runtime demand rejection in `tkg_pda_acceptance`. |

---

## 5. Strict Safety Rules Enforced

1. **Pre-existing Active Session Protection**: If a target machine already has an active session not owned by the current acceptance run, the runner rejects the phase with `BLOCKED — PREEXISTING_ACTIVE_SESSION` and `ENVIRONMENT_BUSY`. Pre-existing sessions are never mutated or ended.
2. **Exact Session Ownership Tracking**: Acceptance harnesses generate unique run GUIDs (`acceptance:<runGuid>:...`) and store exact created session IDs. Cleanup touches only those exact IDs.
3. **No Blanket Machine Cleanup**: Statements of the form `DELETE ... WHERE machine_id = ...` are forbidden on persistent databases.
4. **Commissioning Boundary Preservation**: Customer navigation profiles in `tkg_pda_dev` must remain `DRAFT`. Synthetic published profiles and trial shared endpoint bindings belong exclusively in `tkg_pda_acceptance`.
5. **Exact Snapshot Restoration**: Any allowed temporary state change must snapshot original values first and restore exact values in `finally`.

---

## 6. What PASS Means vs. FIELD READY

- **`ACCEPTANCE SUITE SAFE — LOCAL E2E PASS — FIELD COMMISSIONING INPUTS PENDING`**:
  Persistent acceptance preserved existing business state, and the isolated synthetic paths plus the configured API suite passed. This is a local software result.
- **`ENVIRONMENT_BUSY`**:
  A pre-existing active session or other business operation blocked a phase. The suite does not end that session, does not delete its requests, and does not clean the machine.
- **`ACCEPTANCE SUITE SAFETY FAIL`**:
  A persistent-state safety check failed, or a required phase did not pass.
- **FIELD READY is a separate, later state.** It needs the customer's published navigation, real FG/RETURN/EMPTY bindings, geometry and calibration, the SEALER V3 layout, container metadata for the blocked ALT rows, and the final production-end priorities. This suite does not invent those inputs.
