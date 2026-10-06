# Generates db/seeds/generated/003_tkg_rds_variant_commissioning.sql
# from config/commissioning/tkg_rds_variant_map_naming.json.
# Names are copied. They are not recomputed.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourcePath = Join-Path $projectRoot 'config\commissioning\tkg_rds_variant_map_naming.json'
$targetPath = Join-Path $projectRoot 'db\seeds\generated\003_tkg_rds_variant_commissioning.sql'
$registry = Get-Content -Raw -LiteralPath $sourcePath | ConvertFrom-Json

function Sql-Literal([string]$value) {
    if ($null -eq $value) { return 'NULL' }
    return "'" + $value.Replace("'", "''") + "'"
}

$families = @(
    @{ Key = 'machinePositions'; Family = 'MACHINE_POSITION' },
    @{ Key = 'preparation'; Family = 'PREPARATION' },
    @{ Key = 'emptyBuffers'; Family = 'EMPTY' },
    @{ Key = 'nonFinishedReturns'; Family = 'RETURN' },
    @{ Key = 'finishedGoods'; Family = 'FINISHED_GOODS' },
    @{ Key = 'parks'; Family = 'PARK' },
    @{ Key = 'chargers'; Family = 'CHARGE' }
)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('-- Generated from config/commissioning/tkg_rds_variant_map_naming.json.')
[void]$sb.AppendLine('-- Do not rename customer machines, variants, or parts in this file.')
[void]$sb.AppendLine('-- Planned WorkSite names are reserved here. They are not live RDS WorkSites.')
[void]$sb.AppendLine('-- Replay keeps an advanced commissioning_state and does not publish the profile.')
[void]$sb.AppendLine('BEGIN;')
[void]$sb.AppendLine('')

$bindingCount = 0
$planCount = 0
foreach ($variant in $registry.variants) {
    $area = $variant.mapName
    if (-not $area.EndsWith('_MAP')) {
        throw "Map name does not end with _MAP: $($variant.mapName)"
    }
    $area = $area.Substring(0, $area.Length - 4)
    $switch = [string]$variant.reservedNames.switchStations[0]
    $layoutCode = [string]$variant.sourceLayoutCode
    $layoutSql = if ([string]::IsNullOrWhiteSpace($layoutCode)) { 'NULL' } else { Sql-Literal $layoutCode }
    $code = Sql-Literal $variant.navigationProfileCode
    $machine = Sql-Literal $variant.machineCode
    $variantCode = Sql-Literal $variant.variantCode

    [void]$sb.AppendLine(@"
INSERT INTO master.navigation_profiles (
    machine_id, code, name, version, status,
    base_map_name, robot_map_name, route_profile_name, stitch_profile_name, config_json)
SELECT m.id, $code, $code, 1, 'DRAFT',
       $(Sql-Literal $variant.mapName), $(Sql-Literal $variant.mapName),
       $(Sql-Literal $variant.routeProfileName), $(Sql-Literal $variant.stitchProfileName),
       jsonb_build_object(
           'commissioningRegistry', 'tkg_rds_variant_map_naming_v1',
           'commissioningState', 'PLANNED',
           'geometryVerified', false,
           'rdsAreaName', $(Sql-Literal $area),
           'switchTransitionStation', $(Sql-Literal $switch),
           'layoutBindingState', $(Sql-Literal $variant.layoutBindingState),
           'sourceLayoutCode', $layoutSql)
FROM master.machines m
WHERE m.code = $machine
ON CONFLICT (machine_id, code, version) DO UPDATE SET
    name = EXCLUDED.name,
    base_map_name = EXCLUDED.base_map_name,
    robot_map_name = EXCLUDED.robot_map_name,
    route_profile_name = EXCLUDED.route_profile_name,
    stitch_profile_name = EXCLUDED.stitch_profile_name,
    config_json = master.navigation_profiles.config_json || EXCLUDED.config_json
WHERE master.navigation_profiles.status = 'DRAFT';

INSERT INTO master.variant_navigation_profile_assignments (
    variant_id, machine_id, navigation_profile_id, is_default)
SELECT v.id, v.machine_id, np.id, false
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = $variantCode
JOIN master.navigation_profiles np ON np.machine_id = m.id AND np.code = $code AND np.version = 1
WHERE m.code = $machine
ON CONFLICT (variant_id, navigation_profile_id) DO NOTHING;
"@)

    foreach ($family in $families) {
        $names = @($variant.reservedNames.($family.Key))
        $ordinal = 0
        foreach ($siteName in $names) {
            $ordinal++
            $planCount++
            $site = Sql-Literal ([string]$siteName)
            $familySql = Sql-Literal $family.Family
            [void]$sb.AppendLine(@"
INSERT INTO integration.rds_worksite_plans (
    navigation_profile_id, planned_rds_site_id, object_family, ordinal,
    commissioning_state, geometry_verified)
SELECT np.id, $site, $familySql, $ordinal, 'PLANNED', false
FROM master.machines m
JOIN master.navigation_profiles np ON np.machine_id = m.id AND np.code = $code AND np.version = 1
WHERE m.code = $machine
ON CONFLICT (navigation_profile_id, planned_rds_site_id) DO UPDATE SET
    object_family = EXCLUDED.object_family,
    ordinal = EXCLUDED.ordinal,
    updated_at = now();
"@)
        }
    }

    foreach ($binding in @($variant.slotBindings)) {
        $bindingCount++
        $parts = @($binding.partCodes | ForEach-Object { Sql-Literal ([string]$_) }) -join ', '
        if ([string]::IsNullOrWhiteSpace($parts)) { $parts = '' }
        $metadata = "jsonb_build_object('partCodes', jsonb_build_array($parts), 'geometryVerified', false)"
        [void]$sb.AppendLine(@"
INSERT INTO integration.layout_slot_rds_plan_bindings (
    layout_slot_id, navigation_profile_id, worksite_plan_id, logical_slot_code,
    business_role, source_layout_code, metadata_json)
SELECT s.id, np.id, plan.id, $(Sql-Literal $binding.logicalSlotCode),
       $(Sql-Literal $binding.businessRole), $(Sql-Literal $variant.sourceLayoutCode), $metadata
FROM master.machines m
JOIN master.layouts l ON l.machine_id = m.id AND l.code = $(Sql-Literal $variant.sourceLayoutCode)
JOIN master.layout_slots s ON s.layout_id = l.id AND s.code = $(Sql-Literal $binding.logicalSlotCode)
JOIN master.navigation_profiles np ON np.machine_id = m.id AND np.code = $code AND np.version = 1
JOIN integration.rds_worksite_plans plan
  ON plan.navigation_profile_id = np.id
 AND plan.planned_rds_site_id = $(Sql-Literal $binding.plannedWorksite)
WHERE m.code = $machine
ON CONFLICT (layout_slot_id, navigation_profile_id) DO UPDATE SET
    worksite_plan_id = EXCLUDED.worksite_plan_id,
    logical_slot_code = EXCLUDED.logical_slot_code,
    business_role = EXCLUDED.business_role,
    source_layout_code = EXCLUDED.source_layout_code,
    metadata_json = EXCLUDED.metadata_json,
    updated_at = now();
"@)
    }
}

[void]$sb.AppendLine(@"

DO `$`$
DECLARE
    profile_count integer;
    map_count integer;
    code_count integer;
    area_count integer;
    plan_count integer;
    binding_count integer;
    live_name_collisions integer;
    sealer_bindings integer;
BEGIN
    SELECT count(*) INTO profile_count
    FROM master.navigation_profiles
    WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(DISTINCT robot_map_name) INTO map_count
    FROM master.navigation_profiles
    WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(DISTINCT code) INTO code_count
    FROM master.navigation_profiles
    WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(DISTINCT config_json ->> 'rdsAreaName') INTO area_count
    FROM master.navigation_profiles
    WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(*) INTO plan_count
    FROM integration.rds_worksite_plans p
    JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
    WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(*) INTO binding_count
    FROM integration.layout_slot_rds_plan_bindings b
    JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id
    WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(*) INTO live_name_collisions
    FROM integration.rds_worksite_plans p
    JOIN integration.rds_worksites w ON w.rds_site_id = p.planned_rds_site_id
    JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
    WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
    SELECT count(*) INTO sealer_bindings
    FROM integration.layout_slot_rds_plan_bindings b
    JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id
    WHERE np.code = 'TKG_SEALER_V03_NAV';

    IF profile_count <> $($registry.variants.Count)
       OR map_count <> $($registry.variants.Count)
       OR code_count <> $($registry.variants.Count)
       OR area_count <> $($registry.variants.Count)
       OR plan_count <> $planCount
       OR binding_count <> $bindingCount
       OR live_name_collisions <> 0
       OR sealer_bindings <> 0 THEN
        RAISE EXCEPTION
            'RDS variant commissioning seed mismatch: profiles %, maps %, codes %, areas %, plans %, bindings %, live collisions %, sealer bindings %',
            profile_count, map_count, code_count, area_count, plan_count, binding_count, live_name_collisions, sealer_bindings;
    END IF;
END `$`$;

COMMIT;
"@)

[System.IO.File]::WriteAllText($targetPath, $sb.ToString())
Write-Host "Wrote $targetPath profiles=$($registry.variants.Count) plans=$planCount bindings=$bindingCount"
