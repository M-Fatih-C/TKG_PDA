# Role
- You implement explicit work packages.
- Do not redesign architecture or expand customer scope.
- Stop after the requested package.

# Architecture
Flutter / React
-> ASP.NET Core API
-> Application / Domain
-> PostgreSQL
-> RDS Adapter
-> SEER RDS

Clients never communicate directly with RDS.

SQL migrations are schema source of truth.
Do not introduce EF migrations.

# Frozen domain rules
Machine -> Variant -> Product Group -> Related/Alt Part.
Product Group != Variant.

occupancy:
VACANT / OCCUPIED / UNKNOWN

load:
EMPTY / FULL / UNKNOWN

RESERVED is never occupancy.

Transport operations:
BRING_EMPTY
BRING_FULL
REMOVE_FULL
REMOVE_EMPTY

BRING_FULL was added on 15 September 2026 with explicit customer approval,
for warehouse-to-machine sub-part feed. The other three keep their exact
meaning. Do not add a fifth without the same approval.

Unknown customer mappings remain NULL.

# RDS safety
Never invent production:
- WorkSite labels
- fork heights
- geometry
- LOAD/UNLOAD values
- robot mappings
- recognition parameters

Never dispatch a physical robot unless the task explicitly authorizes it.

Normal development must not enable RDS dispatch.

# Security
Never print or commit:
- passwords
- JWT secrets
- RDS integration keys
- callback keys

# Development workflow
- Serena-first navigation.
- Read only necessary symbols/files.
- Do not use autonomous subagents.
- Do not refactor unrelated code.
- Run affected tests, not every historical regression suite.
- Keep final reports concise.
- Draft PRs are the normal review boundary.

# Current identity contract
Admin:
username + password

Operator production:
employeeNumber + device
NO password

Legacy Operator:
username + password + device

Legacy Operator username login remains temporarily supported in backend.

employee_no:
- string
- trimmed
- case-insensitive identity
- leading zeros significant
- globally nullable
- normalized unique when present

# Current repository state
Implemented on the current line: employee identity, part-first operation, alt-part demand, warehouse preparation, finished-container flow, temporary finished-goods areas, self-hosted delivery notifications, transport evidence, and production-end priority configuration.

Still unresolved outside this repository:
- production-end priority numbers and the exact end-production task policy
- real names for the temporary finished-goods areas
- barcode hardware and format
- M4 API documentation
