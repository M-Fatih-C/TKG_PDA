# RDS physical WorkSite occupancy gate

Scope: physical capacity and execution safety only. No M4, park/charge, customer commissioning, schema migration or automatic inventory correction.

## Verified source and adapter semantics

The supplied SEER contract is archived at `TKG_PDA_Arsiv/dokumanlar/docs/references/rds/RdsApi_en.md`, lines 91–97 and 120–136. The customer-source copy at `TKG_PDA_Arsiv/musteri-kaynaklari/RDS DÖKÜMANLARI/RdsApi/RdsApi_en.md` provides the same contract. `RdsUserManual_en.md`, lines 1480–1483, describes Filled/Unfilled as occupied/unoccupied and explicitly states that other robots cannot use a locked space. The API also describes releasing task-related WorkSite locks. Both API copies have SHA-256 `9db6517eb226db9e32e1cf33de340ed54dbde74615d170869bdb9c9689ab22ee`. The API contract was read and its semantics reported before implementation; the manual was subsequently checked as corroboration. No live RDS request was made.

| Field | Verified meaning | Unknown handling |
| --- | --- | --- |
| filled | 0 unoccupied; 1 occupied | Missing, null, Boolean, non-integral or invalid numeric text parses to null; other integers remain uninterpreted evidence. |
| locked | 0 unlocked; 1 locked | Missing/unrecognized values cannot prove destination availability. Worksite locks are distinct from robot maintenance locks. |
| type | 1 physical; 0 logical | Logical/unknown types cannot provide physical capacity. The contract does not define a different filled encoding for logical sites. |
| disabled | 0 enabled; 1 disabled | Destination requires known 0. |
| syncFailed | 0 no sync failure; 1 sync failure | Destination requires known 0. |

`RdsReadClient.GetWorkSitesAsync` reads the exact WorkSite fields and clones the raw item. `PostgresRdsIntegrationStore.SyncWorkSitesAsync` writes those nullable values and the raw JSON unchanged. Numeric strings are accepted by the existing adapter. Filled never proves load, part, container identity, or warehouse readiness. The adapter tests now explicitly cover wire types and out-of-contract integer values.

## Defect and implementation

Both shared destination allocation and Production End preflight previously checked only successful-sync presence, disabled and syncFailed, treating null flags as zero. They ignored filled, locked, physical type and freshness. The Mamul projection repeated that limited check and ignored global reservations.

`RdsPhysicalAvailabilityPolicy` is the single application policy. A destination requires an exact profile binding, physical type, known filled=0, locked=0, disabled=0, syncFailed=0 and successful evidence within the existing `Rds:WorkSiteFreshnessSeconds` setting (default 300 seconds). Missing or unsupported evidence fails closed. No timeout was invented.

FG, NON_FINISHED_RETURN and EMPTY_RETURN use this policy. Candidates are ordered by canonical business code; physical rows/bindings are held FOR SHARE in the creation transaction, the existing `shared_destination:{businessCode}` transaction advisory lock is acquired, and global active reservation is rechecked before allocation. End preflight uses the identical selector and requires the actual number of free endpoints before any child/run/session mutation can commit.

Another NAV never supplies an alternative binding. A current physical filled/locked observation for another representation of the same canonical destination vetoes allocation. Stale, failed or logical observations cannot prove that contradiction; the selected profile must independently have complete current evidence. Reservation scope remains global across NAVs.

EMPTY_SOURCE remains the established nonsingle-capacity pool model. Repository code proves that abstraction, but supplied topology does not prove that one WorkSite bit describes availability of the entire pickup pool. Its existing source health and capacity behavior therefore remains unchanged; no filled=1 pickup requirement or single-owner reservation was added. Legacy mapped EMPTY_STORAGE slots still have ordinary slot reconciliation.

Mamul FULL and EMPTY remain visible. FULL projection uses physical FG availability plus global reservations; creation repeats the transactional gate. Physical occupancy yields capacity-unavailable, not configuration-missing. Exact missing binding/store configuration retains the existing configuration contract.

Mapped layout slots are compared with their TKG occupancy before request creation and again before dispatch in both Wind and native modes. UNKNOWN/stale and proven contradictions block execution. TKG slot rows are never rewritten from telemetry. Same-key replays still return their existing request. Dispatch also rereads canonical destination observations, including other NAV representations, after allocation.

## Read-only visibility and errors

`GET /api/v1/admin/rds/physical-evidence?navigationProfileId=<id>` is Admin-only and executes a repeatable-read, read-only transaction. It returns business code/class, slot ID, TKG occupancy, reservation, mirrored and external WorkSite identity/name, raw numeric evidence, sync timestamp, physical status and consistency. Shared endpoints have no slot-runtime occupancy row; they explicitly report `businessOccupancy=null` / `BUSINESS_STATE_NOT_TRACKED` rather than invent one. Operator projection carries only safe readiness/consistency information.

| Condition | Contract |
| --- | --- |
| All current destinations filled or reserved / contradictory NAV | Existing FINISHED_GOODS / NON_FINISHED_RETURN / EMPTY_RETURN_CAPACITY_UNAVAILABLE |
| Disabled / locked / failed sync / stale / nonphysical | Existing RDS_WORKSITE_DISABLED / LOCKED / SYNC_FAILED / STALE / NOT_PHYSICAL |
| Unknown numeric evidence or business occupancy | New RDS_WORKSITE_STATE_UNKNOWN |
| TKG VACANT + physical filled, or OCCUPIED + physical empty | New RDS_WORKSITE_OCCUPANCY_CONFLICT |
| VACANT + empty, OCCUPIED + filled | CONSISTENT; availability still separately checks health/lock |
| Stale/missing/unsupported occupancy evidence | PHYSICAL_STATE_UNKNOWN |

Flutter presents actionable Turkish messages for the new errors and for locked/stale/nonphysical evidence. Physical errors are not added to the configuration-missing classifier.

## Verification

Final non-live regression: **1,565 distinct tests passed, 0 failed, 0 skipped**. Repeated focused cases are not counted twice.

| Suite | Passed |
| --- | ---: |
| Domain | 146 |
| Application | 356 |
| Authentication | 15 |
| PostgreSQL integration | 83 |
| Flutter | 299 |
| Admin Web | 66 |
| Non-live DB-backed API | 590 |
| Browser assertion units | 10 |

The nine acceptance safety checks also pass. Flutter analysis is clean; Admin lint and TypeScript/Vite build pass. A final three-case diagnostic recheck covers the nullable shared-endpoint business occupancy output after the full suite. Earlier failing runs are retained locally as diagnostic evidence and are not counted as passes.

Canonical acceptance: **13 PASS, 1 BLOCKED, 1 SKIP**, declared exit **2**, elapsed **481.2 seconds**. Persistent Mamul matrix is blocked before mutation because it cannot fabricate physical observations; the browser preflight finds RDS_WORKSITE_STALE and writes a fresh SKIP report before session/request mutation. All mandatory isolated workflows and 590/590 API assertions pass. Existing busy-session guards are unchanged. The pg_ctl shutdown-wait warning occurs after successful assertions/integrity verification; no phase-04 listener remains.

The persistent physical matrix is not labelled PASS merely because an action is correctly disabled. Business state combinations and physical contradictions are proven in isolated API tests. Final status: **CODE PASS — LOCAL E2E ENVIRONMENT BLOCKED (physical evidence)**. This is not field commissioning approval.

Tests cover numeric parsing, complete destination flags, freshness boundary, filled/unknown/locked/disabled/sync failure, reservation exclusion, canonical concurrent ownership, contradictory cross-NAV observations, deterministic mixed selection, FG/RETURN/EMPTY next-free and all-full behavior, three-mapping/two-vacancy atomic End, mapped-slot reconciliation without mutation, Admin-only diagnostic access, execution rechecks, and unchanged EMPTY_SOURCE pool semantics.

Persistent evidence is saved locally in `scratch/occupancy-persistent-before.json` and `scratch/occupancy-persistent-after.json`; protected session evidence in `scratch/occupancy-session-before.json` and `scratch/occupancy-session-after.json`. These snapshots are not committed. Existing field data, priorities, ALT metadata, SEALER V3 and the protected SH-01-A session must remain unchanged. The 13:20:33 +03 implementation baseline already records it as ENDED at 13:17:24.02068 +03, despite the request describing an ENDING session. No restoration to ENDING or automatic recovery was performed.


All original rows in the fourteen-table snapshot are exactly preserved. WorkSites **3763 → 3763**, shared bindings **0 → 0**, transport requests **13 → 13**, slot runtime **189 → 189**. Production sessions **147 → 176**: the 29 additions all have an acceptance-owned start key and are ENDED; no original session row changed. All scoped protected-session runs/items/requests/events/audits/RDS records/slots/runtime/preparation/store evidence is identical before/after, excluding only observation time.

The same requested branch is used. The final commit/remote SHA and existing Draft PR URL are reported in the delivery message and local final report. No customer publication, physical binding, geometry, SEALER V3, ALT metadata, priority or existing mirrored evidence was changed.
