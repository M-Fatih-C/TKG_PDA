# TKG PDA Agent Rules

## Role and scope

- Act as an implementation agent, not as the project architect.
- Preserve the established design and customer scope. Do not add adjacent features or redesign completed phases.
- Never invent customer-data or RDS mappings. Leave unknown values unresolved and report them.

## Architecture

Keep dependencies flowing in this direction:

```text
Flutter / React
  -> ASP.NET Core API
  -> Application / Domain
  -> PostgreSQL
  -> RDS adapter
  -> SEER RDS
```

Clients must never call RDS directly. State mutations must pass through the relevant application/domain boundary.

## Database

- SQL migrations are the schema source of truth.
- Do not create or use EF migrations.
- Preserve existing migrations unless the scoped task explicitly requires a reviewed schema change.

## Domain

- The customer hierarchy is `Machine -> Variant -> Product Group -> Part / Related Part / Alternative Part`. A Product Group is not a Variant.
- Occupancy states are `VACANT`, `OCCUPIED`, and `UNKNOWN`.
- Load states are `EMPTY`, `FULL`, and `UNKNOWN`.
- `RESERVED` is not an occupancy state.
- Supported transport operations are `BRING_EMPTY`, `BRING_FULL`, `REMOVE_FULL`, and `REMOVE_EMPTY`.
- `BRING_FULL` is warehouse-prepared full-material delivery. It waits for warehouse readiness before robot dispatch.

## Customer data

Customer Excel/PDF sources are authoritative for:

- machine identifiers;
- variants;
- parts and part relationships;
- confirmed physical layouts.

Do not guess missing values and do not hard-code customer master data in Flutter.

## RDS safety

Never invent WorkSite labels, robot mappings, fork heights, pickup/drop geometry, recognition parameters, or production task labels. Do not dispatch a live robot unless the user has explicitly authorized that exact test and scope.

RDS alarms are read-only through `GET /api/agv-report/core`. TKG may store its own observation of a successful snapshot. That history is not an RDS incident log, and a failed read does not clear it. Alarm telemetry never changes occupancy or load.

## Authentication boundaries

- Production Operator: `employeeNumber + device`, without a password.
- Legacy Operator: `username + password + device`.
- Admin: `username + password`, without mandatory PDA device binding.

Never print or commit passwords, JWTs, RDS keys, callback keys, or other credentials.

## Operator workflow

The normal Operator flow is:

```text
machine -> part -> operation
```

The backend resolves the variant/context, physical machine slot, reservation, and RDS mapping. A normal Operator must not choose a physical slot. Layouts are primarily visualization and status surfaces, not robot maps.

## Part-slot routing

- `master.layout_slot_parts` is the operational source of truth for Part-to-Slot mapping.
- `layout_slots.metadata_json`, including `canonical_part_codes`, is extraction evidence/provenance only. Never use it as authoritative runtime routing.
- Machine/cell display regions are not transport source or destination slots.
- `EMPTY_STORAGE` belongs to backend buffer allocation and must not be selected as the machine-side Part-to-Slot endpoint.

## Workflow and execution

- `TransportBatch` groups independent `TransportRequest` records.
- Each request owns its own operation, idempotency identity, and RDS identity.
- A batch must remain compatible with the machine's single active variant; it must not switch variants between item configurations.
- Warehouse readiness is separate from transport execution state.

## Testing and work style

- Use repository code and tests as the primary implementation source; use docs for established contracts and constraints.
- Run affected tests in proportion to the change. Do not run every historical suite by default.
- Do not use live RDS or trigger robot motion unless explicitly instructed.
- Keep tasks scoped, avoid unrelated refactors, and do not narrate routine commands.
- Create Draft PRs at review boundaries unless explicitly instructed otherwise.
