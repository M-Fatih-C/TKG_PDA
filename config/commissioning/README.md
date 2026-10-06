# TKG RDS Variant Map / WorkSite Registry

This directory defines the commissioning naming contract before field geometry is finalized.

## Rules

- Customer machine, variant, product, main-part and alt-part identities stay unchanged.
- Every variant has its own RDS map and navigation profile.
- Map: `TKG_<PREFIX>_V<NN>_MAP`
- Navigation profile: `TKG_<PREFIX>_V<NN>_NAV`
- Machine WorkSites: `..._WS_P01..P20`
- Preparation: `..._WS_PREP01..08`
- Empty buffer: `..._WS_EMPTY01..08`
- Non-finished return: `..._WS_RETURN01..08`
- Finished goods: `..._WS_FG01..19`
- Park: `..._WS_PARK01..04`
- Charge: `..._WS_CHG01..04`
- Switch area/station: `..._SW01..04`, `..._ST01..04`
- Roads: `..._RD001..064`

All coordinates, road geometry and switch polygons are placeholders until on-site verification.
Names are intended to remain stable during commissioning.

## Important DB binding requirement

Some customer variants share the same physical `master.layout`, while the commissioning contract requires a different RDS map/navigation profile per variant.

The current `integration.layout_slot_rds_mapping` is keyed only by `layout_slot_id`, so it cannot represent:

```
same layout_slot
+ V01 navigation profile -> V01 WorkSite
+ V02 navigation profile -> V02 WorkSite
```

Before live variant-specific WorkSite binding is enabled, mapping must become navigation/profile scoped (or an equivalent canonical model must be implemented). The legacy mapping must not be silently overwritten.

## Source

Authoritative registry: `tkg_rds_variant_map_naming.json`.

SEALER V3 currently has product/variant master data but no source-backed physical layout assignment. Its map/profile namespace is reserved as PLANNED; its field/layout binding remains pending.
