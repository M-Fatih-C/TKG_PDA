# TKG final presentation readiness

Generated from `tkg_pda_dev` on 127.0.0.1:55450 and from the public-API alignment tests. Do not treat a task-template row or a planned WorkSite name as execution readiness.

A selectable PDA option is executable only when its master data is complete. Shared physical endpoints resolve by business code under the request navigation profile, the same identity the commissioning report evaluates. Layout slots remain machine-side bookkeeping. They are not a second physical destination.

## Git

- Starting HEAD: `6094baf8ac51ed066fb582a9fbcb30adb6831c6b`
- Branch: `agent/tkg-rds-map-worksite-registry`
- Final HEAD: the commit that adds this document
- Working tree: clean after that commit is pushed

## Master Data

Queried from the developer database. A clean integration database after seed replay has `integration.rds_worksites = 0`. The developer database may contain simulator discoveries; those rows are not customer production mappings and are not used as commissioned endpoints. Shared-endpoint bindings on this database: 0.

| Item | Count |
| --- | ---: |
| Machines | 9 |
| Variants | 25 |
| Source-backed variants | 24 |
| Product groups | 51 |
| ALT_PART relations | 140 |
| Requestable ALT_PARTs | 115 |
| Non-requestable ALT_PARTs (`CONTAINER_TYPE_MISSING`) | 25 |
| Preparation areas | 4 |
| Finished-goods locations | 19 (`FG-01`..`FG-19`) |
| Active source-backed templates | 24 per operation, 96 total |
| Registry profiles DRAFT | 25 |
| Geometry verified | 0 |
| Profile shared-endpoint bindings | 0 |

Lot quantity is not required to create a transport. Missing container type is.

## Machine Coverage

| Machine | Variants | Main products | ALT_PART | Requestable | Blocked by source data | Commissioning |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| SH-01-A | 5 | 9 | 25 | 25 | 0 | NOT_READY |
| SH-01-B | 3 | 6 | 14 | 14 | 0 | NOT_READY |
| SH-01-P1--SH-01-P2 | 2 | 2 | 14 | 12 | 2 | NOT_READY |
| SH-02-P1 -SH-02-P2 | 2 | 2 | 16 | 14 | 2 | NOT_READY |
| SEALER | 3 | 14 | 14 | 14 | 0 | NOT_READY. V3 is `PENDING_SOURCE_LAYOUT` |
| PAH-01-A | 2 | 4 | 16 | 10 | 6 | NOT_READY |
| 412B | 3 | 6 | 15 | 12 | 3 | NOT_READY |
| PAH-02-A | 2 | 4 | 18 | 10 | 8 | NOT_READY |
| GAR | 3 | 4 | 8 | 4 | 4 | NOT_READY |

Every source-backed variant is blocked by unpublished NAV, unverified geometry, and missing preparation, empty, finished-goods, and return mappings. That is field commissioning, not a missing template.

## Business Flow

| Flow | Result |
| --- | --- |
| Terminal binding | PASS. Existing terminal-machine tests remain in the API suite. |
| Production selection | PASS. Part-first session start is unchanged. |
| ALT_PART request | PASS. Public part-demand creates `BRING_FULL` and stores the active navigation profile. |
| Incomplete ALT_PART | PASS. Missing container type is not selectable and the API rejects it before insert. |
| Repeat request replacement | PASS. Occupied full material creates `REMOVE_FULL` then dependent `BRING_FULL`. |
| Batch demand | PASS. Existing batch tests remain green. |
| Warehouse preparation | PASS. Public pallet place/ready on Hazırlık Alanı 1 resolves `WS_PREP01` under the request NAV. |
| Mamul Kasa | PASS. Public Kasa Doldu stores `destination_business_code = FG-01`. |
| Active progress | PASS. Existing active-transport projection tests are unchanged. |
| Production End | PASS. Public end-production on a registry profile creates main `FG-01`, non-finished `_WS_RETURN01`, and empty `_WS_EMPTY01`. The priority numbers in that test are a labeled TEST/DEMO fixture, not customer values. |
| Finished Goods | PASS. Completion writes one store unit at `FG-01`. A second completion does not deliver again. |
| Role split | PASS. Semi-finished preparation stays separate from finished goods. |
| Notification | PASS. Existing finished-goods notification tests remain. This alignment fixture had no registered finished-goods device, so it asserts the store event rather than an outbox row. |

## Execution Alignment

When the active navigation profile belongs to `tkg_rds_variant_map_naming_v1`:

| Operation | Runtime identity | Commissioning identity |
| --- | --- | --- |
| BRING_FULL | Ready preparation pallet area mapped to that profile | `PREPARATION` / `WS_PREP01`..`04` |
| BRING_EMPTY | `EMPTY_SOURCE` binding. No `EMPTY_STORAGE` slot is allocated | `EMPTY_SOURCE` |
| REMOVE_FULL Mamul | `destination_business_code` `FG-xx`, then the same `ops.store_locations` row | `FINISHED_GOODS` |
| REMOVE_FULL non-finished | `NON_FINISHED_RETURN` business code under that NAV | `NON_FINISHED_RETURN` |
| REMOVE_EMPTY | `EMPTY_RETURN` business code. Destination slot stays null | `EMPTY_RETURN` |

A business code is not resolved through another profile's WorkSite. A disabled or sync-failed WorkSite is not allocated. Requests whose navigation is not a registry profile keep the previous layout-slot path so historical and non-commissioned tests stay valid.

## E2E Proof

`PresentationExecutionAlignmentApiTests` creates requests through the public API:

- `BRING_FULL` after warehouse ready resolves a `WS_PREP01` WorkSite on `TKG_SH01A_V01_NAV` and not a V2 site.
- Repeated full ALT_PART `REMOVE_FULL` destination ends with `_WS_RETURN01` and is not an FG code.
- Repeated empty container `REMOVE_EMPTY` destination ends with `_WS_EMPTY01` and has no destination slot.
- Mamul `REMOVE_FULL` destination is `FG-01`. Completion inserts one `ops.store_units` row at `FG-01`.
- Public production end creates three cleanup requests. The main full container resolves `FG-01` and one store unit. The ALT full container resolves `_WS_RETURN01`. The empty container resolves `_WS_EMPTY01`. Completing the chain ends the session. No second store unit is created.
- `integration.rds_tasks` stays 0. No robot task is sent.

## Tests

Counts below are from this pass. A harness `pg_ctl` cleanup warning, when the suite result is already PASS, is not an application failure.

| Suite | Result |
| --- | --- |
| Release build | 0 warnings, 0 errors |
| Domain | 146 passed |
| Application | 320 passed |
| Auth | 15 passed |
| Phase 03 | 78 passed, result PASS, `rds_tasks` 0, `rds_worksites` 0 |
| Phase 04 API integration | 329 passed, 0 failed |
| Phase 04 verification | `production_migration_replay = UNCHANGED`, `production_orphans = 0`, `rds_tasks = 0`, `rds_worksites = 0`, result PASS. The harness then exited 1 because `pg_ctl` did not stop before the temp directory delete. That is teardown, not an application failure. |
| Alignment public API | included in the 329: bring, remove chains, production end, and source-data classification |
| Node | 14 passed |
| Flutter analyze | No issues found |
| Flutter tests | 285 passed |
| AdminWeb | oxlint clean, 66 tests passed, production build succeeded |

## UI

- PDA: an ALT_PART with `requestable = false` shows `Taşıma kabı bilgisi bekleniyor` and does not call the mutation.
- Admin `/commissioning`: five badges — Malzeme Getir, Boş Kasa Getir, Mamul Kasası Götür, Alt / Yarı Mamul Dönüşü, Boş Kasa Götür. `✓ Hazır` only when that chain is READY.

## Field blockers

`SOFTWARE_BLOCKER`: none identified by the alignment tests.

`EXTERNAL_PENDING`:

- Live WorkSite discovery and exact mappings for PREP01–04, the real empty subset, FG-01..FG-19, and the real return subset.
- NAV publication and field geometry confirmation.
- SEALER V3 source layout.
- Container type for the 25 ALT_PART rows that have none.
- Production-end customer priority numbers.
- Final FG display names.
- Barcode hardware and format.
- M4 park/charge behavior. PARK and CHARGE are not material-flow endpoints.

## Final status

PRESENTATION READY — FIELD COMMISSIONING INPUTS PENDING
