# Seed Policy

> **Üretim hattı bu depoda değil.** `tools/phase_02a`, `tools/phase_02b`,
> `data/generated` ve `tests/phase_02a` klasörleri 16 Eylül 2026'da
> `Masaüstü/TKG_PDA_Arsiv/musteri-veri-hatti` altına taşındı. Aşağıdaki komutları
> çalıştırmak için o klasörü geçici olarak depo köküne geri koyun. Hattın ürettiği
> SQL dosyaları `db/seeds/generated/` altında, yerlerinde duruyor.


Phase 01 seeds only stable baseline configuration contained in the initial migration:

- four system roles,
- three logistics task types,
- nine customer machine labels,
- the current machine-level variant counts.

Customer part codes, container types, layout slots, product groups, planning metrics, RDS WorkSite IDs, SEER/RDS map/profile identifiers, route/stitch identifiers and task labels are intentionally not guessed.

Phase 02A implements the textual customer-data portion as a deterministic, source-traceable generated-seed pipeline. Real parts with missing planning inputs are still imported; missing inputs and uncomputable derived metrics remain `NULL`.

`db/seeds/006_blank_alt_part_euro_pallet.sql` records the customer-approved
operational rule dated 27 October 2026: when an `ALT_PART` container field is
blank in the source, its transport container is `EURO_PALET`. The supplemental
seed runs after the generated source seed, preserves every explicit container
code, and keeps the generated extraction evidence unchanged.

## Phase 02A generated-seed workflow

Run with the configured Python environment containing `openpyxl==3.1.5`:

```powershell
python tools/phase_02a/pipeline.py --project-root .
```

The command reads the eight customer Excel files without modifying them and regenerates:

- normalized JSON/CSV files under `data/generated/phase_02a/`,
- the complete data-quality report,
- `db/seeds/generated/002a_customer_textual_master_data.sql`.

Generated files must not be edited by hand. Change extraction/normalization rules in `tools/phase_02a/pipeline.py`, rerun the pipeline, review the issue report, then run the tests.

```powershell
python -m unittest discover -s tests/phase_02a -p "test_*.py" -v
pwsh tests/phase_02a/run_postgres_integration.ps1
```

Database application order:

1. `db/migrations/001_initial_schema.sql`
2. `db/seeds/generated/002a_customer_textual_master_data.sql`

The generated seed is idempotent and resolves baseline machine/variant UUIDs through customer codes. It contains no layout geometry, RDS IDs, route/stitch identifiers or guessed task labels. The separate physical-layout workflow is documented below.

## Phase 02B generated physical-layout workflow

Run with the configured Python environment containing `openpyxl==3.1.5` and `Pillow==12.3.0`:

```powershell
python tools/phase_02b/pipeline.py --project-root .
```

The command reads the five physical-layout workbooks without saving or modifying them and regenerates:

- normalized JSON/CSV and the complete issue report under `data/generated/phase_02b/`,
- one deterministic visual review PNG per layout under `data/generated/phase_02b/review/`,
- `db/seeds/generated/002b_customer_physical_layouts.sql`,
- `db/seeds/generated/002c_customer_layout_slot_part_assignments.sql` — the curated `master.layout_slot_parts` mapping (see below).

Generated files must not be edited by hand. Change extraction boundaries or normalization rules in `tools/phase_02b/pipeline.py`, rerun the pipeline, visually inspect all review PNGs against the customer sheets, then run:

```powershell
python -m unittest discover -s tests/phase_02b -p "test_*.py" -v
pwsh tests/phase_02b/run_postgres_integration.ps1
```

Complete database application order:

1. `db/migrations/001_initial_schema.sql`
2. `db/seeds/generated/002a_customer_textual_master_data.sql`
3. `db/seeds/generated/002b_customer_physical_layouts.sql`
4. `db/migrations/005_layout_slot_part_mapping.sql`
5. `db/seeds/generated/002c_customer_layout_slot_part_assignments.sql`

The Phase 02B seed is idempotent and resolves machines, variants, parts, and container types through the Phase 01/02A customer baseline. It inserts no new canonical part/container data. `ui_*` values are normalized PDA display geometry only. Source-backed nominal dimensions in `master.container_types` may be retained in slot provenance, but are not robot pose, clearance, route or docking geometry. No RDS WorkSite, robot/map coordinate, route, stitch, navigation profile, or SEER map data is generated.

Of the 183 generated `master.layout_slots` rows, 172 are logistics positions and 11 are machine/cell display regions marked with `slot_role = 'MACHINE'`. These 11 rows are visual/physical references only. Future workflow generation must exclude them from transport source/destination selection; the existing `slot_role` discriminator is sufficient for this baseline, so no schema migration is required.

## Part ↔ Layout Slot mapping (`master.layout_slot_parts`)

Automatic machine-side slot resolution (Operator UX) needs an explicit, curated relationship between a resolved part and the physical slot(s) it belongs to. `master.layout_slots.metadata_json` (`canonical_part_codes` / `part_association_unresolved` / `confidence`) is automated Excel-extraction evidence with self-reported gaps; it is provenance/data-quality information only and is never consulted for runtime routing or `PART_CONFLICT`. Runtime association and conflict checks use `master.layout_slot_parts` exclusively.

`db/seeds/generated/002c_customer_layout_slot_part_assignments.sql` is generated by the same Phase 02B pipeline (`eligible_slot_part_pairs` in `tools/phase_02b/pipeline.py`) from the identical in-memory evidence it already computes per slot, and is populated **only** where that evidence is unambiguous: `part_association_unresolved` is `False` **and** the resolved part is not flagged as a variant mismatch (present in master data but not planned for that layout's machine/variant). In the current generated baseline, 124 of 172 logistics slots are mapped by 143 Part↔Slot pairs. The remaining 48 are intentionally unmapped: 28 have unresolved part evidence and 20 contain only variant-mismatch evidence. Neither category is guessed into runtime routing. The mapping is many-to-many: a slot may hold several part codes, and a part may appear in multiple slots across different variant-specific layouts.
