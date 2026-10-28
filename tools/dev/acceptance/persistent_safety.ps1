# Read-only snapshots and checked SQL shared by persistent acceptance scripts.
function Invoke-AcceptanceSql {
    param([string]$Sql)
    $value = & $psql -X -h $HostName -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance SQL failed; no PASS may be reported.' }
    return ($value -join "`n").Trim()
}

function Get-PersistentBusinessSnapshot {
    $tables = @('ops.production_sessions', 'ops.transport_requests', 'ops.transport_request_dependencies',
        'ops.slot_runtime_state', 'ops.machine_runtime_state', 'ops.store_units',
        'integration.profile_shared_endpoint_bindings', 'integration.rds_worksites',
        'integration.layout_slot_rds_mapping', 'master.task_templates', 'ops.store_locations', 'master.navigation_profiles',
        'master.production_end_priorities', 'master.production_end_area_priorities', 'audit.client_devices')
    $snapshot = @{}
    foreach ($table in $tables) {
        $snapshot[$table] = @((Invoke-AcceptanceSql "SELECT row_to_json(t)::text FROM $table t ORDER BY row_to_json(t)::text;") -split "`n" | Where-Object { $_ })
    }
    return $snapshot
}

function Assert-PersistentBusinessSnapshot {
    param([hashtable]$Before)
    $after = Get-PersistentBusinessSnapshot
    foreach ($table in $Before.Keys) {
        # A successful acceptance may append its own closed sessions, but every
        # pre-existing complete row (including versions/timestamps) must survive.
        $remaining = [System.Collections.Generic.HashSet[string]]::new([string[]]$after[$table])
        foreach ($row in $Before[$table]) {
            foreach ($line in ($row -split "`n")) {
                if ($line -and -not $remaining.Contains($line)) {
                    throw "Persistent business state changed in $table. Preserve the snapshot for investigation."
                }
            }
        }
    }
    Write-Host 'PASS: Every pre-existing persistent business row is exactly preserved.' -ForegroundColor Green
}

function Assert-PersistentTargetIdle {
    param([string[]]$MachineIds)
    $ids = ($MachineIds | ForEach-Object { "'$_'::uuid" }) -join ','
    $busy = Invoke-AcceptanceSql "SELECT id FROM ops.production_sessions WHERE machine_id IN ($ids) AND ended_at IS NULL UNION ALL SELECT id FROM ops.transport_requests WHERE machine_id IN ($ids) AND status NOT IN ('COMPLETED','FAILED','CANCELLED');"
    if ($busy) {
        Write-Host 'BLOCKED: ENVIRONMENT_BUSY — pre-existing production/transport; no runtime mutation performed.' -ForegroundColor Yellow
        exit 2
    }
}

function Restore-RuntimeRow {
    param([string]$Snapshot)
    if (-not $Snapshot) { throw 'Cannot restore an absent runtime snapshot.' }
    # json_populate_record preserves SQL nulls and exact timestamptz/row_version.
    $literal = $Snapshot.Replace("'", "''")
    Invoke-AcceptanceSql @"
UPDATE ops.slot_runtime_state s
SET occupancy_state = original.occupancy_state, load_state = original.load_state,
    current_part_id = original.current_part_id, current_container_type_id = original.current_container_type_id,
    row_version = original.row_version, updated_at = original.updated_at
FROM json_populate_record(NULL::ops.slot_runtime_state, '$literal'::json) original
WHERE s.layout_slot_id = original.layout_slot_id;
"@ | Out-Null
}
