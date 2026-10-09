# TKG RDS Script

The repository keeps two artifacts:

- `tkg_integration.js`: version-controlled integration logic and test target.
- `tkg_integration_install.template.js`: single-file, paste-ready RDS Script UI installer. Replace the `<...>` placeholders locally before saving it in RDS. Never commit real secrets.

## Is the RDS script still required?

The current TKG stack supports both `RDSCORE_NATIVE_ORDER` and `WIND_TASK`.

Native order dispatch itself uses the RDSCore native APIs and does **not** need the script to create an order. However the complete TKG integration still uses the script for capabilities that are independent of native order creation:

- controlled WorkSite `content` synchronization through `/script-api/tkg/update-content`;
- low-latency `taskDone`, `taskFailed`, `taskStopped` and `workSiteChanged` callbacks;
- explicit `WIND_TASK` execution mode when a task template uses it;
- non-mutating Wind Task validation through `/script-api/tkg/validate-transport`.

For commissioning and production, keep the script installed even when the normal transport engine is `RDSCORE_NATIVE_ORDER`. PostgreSQL/RDSCore reconciliation remains authoritative and the callbacks do not replace it.

## Installation

Use the supported RDS **Script** interface only. Do not edit RDS internal files or databases.

Open:

`rds/scripts/tkg_integration_install.template.js`

and replace:

```js
integrationKey: "<SAME VALUE AS BACKEND Rds__IntegrationKey>",
callbackUrl: "http://<TKG_API_HOST>:<TKG_API_PORT>/api/integrations/rds/events",
callbackKey: "<SAME VALUE AS BACKEND Rds__CallbackKey>"
```

`callbackUrl` must be reachable **from the RDS server**. Do not use `127.0.0.1` unless the TKG API actually runs on the same host.

The template already contains:

```js
function boot() {
  TkgRdsIntegration.install(jj, TKG_RDS_INTEGRATION_CONFIG);
}
```

Paste the complete template into the RDS Script editor, save it, and reload/start the script using the normal RDS UI.

The script registers:

```text
POST /script-api/tkg/validate-transport
POST /script-api/tkg/create-transport
POST /script-api/tkg/update-content

taskDone
taskFailed
taskStopped
workSiteChanged
```

A real Wind Task label and its exact input variables must already exist before using `WIND_TASK`. No tutorial task label is substituted.

## Safe validation

Before enabling live dispatch, validate the script with the non-mutating `validate-transport` endpoint. A deliberately nonexistent label may return `TASK_LABEL_NOT_FOUND`; that still proves the installed route executed and authenticated without creating a task.

Do not test `create-transport` or `update-content` against production WorkSites merely to prove installation.

## Local contract test

```text
node --test rds/scripts/test/tkg_integration.test.js
```
