# Remaining TKG external inputs

Only items that still need a customer, vendor, or field source are listed.

## Production End priorities

`REMOVE_FULL` and `REMOVE_EMPTY` priorities are unset.
The table is `master.production_end_priorities`.
No numeric default is stored.

## Finished-goods location names

Internal identities `FG-01` through `FG-19` exist.
TKG Logistics still has to supply the customer display name for each code.
Replacing a display name must keep the same location id.

Capacity per location is also unknown and remains null.

## Barcode

Whether barcode matching is required is undecided.
No scanner or hardware source, barcode format, or barcode-to-part mapping owner exists.
No barcode client was added.

## M4

Real API documentation is required before any M4 work:

- authentication
- task model
- robot state model
- cancel semantics
- park semantics
- charge semantics

Current RDS execution is unchanged.

## SEALER V3

The physical source layout is still missing.
`TKG_SEALER_V03_NAV` stays `PENDING_SOURCE_LAYOUT`.

## RDS commissioning

Live WorkSite discovery on the installed maps is still required.
Field geometry confirmation is still required before a profile can be verified or promoted.
The confirmation tool records an explicit decision. It does not measure geometry or write RDS geometry.

## RDS script

The controller still needs:

- `integrationKey`
- `callbackKey`
- a reachable callback URL

## A270

The meeting note contains only "Robotun".
The rest of the sentence is required before any behavior can be specified.
