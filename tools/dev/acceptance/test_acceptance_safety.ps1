$ErrorActionPreference = 'Stop'
$checks = @(
    @('init_acceptance_db.ps1', 'tkg_pda_dev'),
    @('init_acceptance_db.ps1', 'arbitrary_database'),
    @('test_synthetic_production_end.ps1', 'tkg_pda_dev'),
    @('test_synthetic_repeated_alt.ps1', 'tkg_pda_dev'),
    @('test_safety_sentinel.ps1', 'tkg_pda_dev')
)
foreach ($check in $checks) {
    $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot $check[0]) -Database $check[1] 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'requires isolated tkg_pda_acceptance database') {
        throw "Expected explicit database safety rejection for $($check[0]) / $($check[1])."
    }
    Write-Host "PASS: $($check[0]) refuses $($check[1]) before SQL."
}
Write-Host "RESULT: PASS ($($checks.Count)/$($checks.Count) explicit safety gates)"
$startup = Get-Content (Join-Path $PSScriptRoot '..\start_local_stack.ps1') -Raw
if ($startup -match '(?im)^\s*(DELETE FROM|TRUNCATE|DROP DATABASE)\b') {
    throw 'Normal persistent startup must not contain broad implicit destructive cleanup SQL.'
}
Write-Host 'PASS: Normal startup has no implicit destructive cleanup SQL.'
foreach ($script in @('test_synthetic_production_end.ps1', 'test_synthetic_repeated_alt.ps1', 'test_safety_sentinel.ps1')) {
    $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot $script) -Database tkg_pda_acceptance -ApiBaseUrl 'http://127.0.0.1:5000' 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'dedicated isolated API') {
        throw "Expected explicit API isolation rejection for $script."
    }
    Write-Host "PASS: $script refuses persistent API port 5000 before SQL/HTTP."
}
Write-Host 'RESULT: PASS (9 safety checks: 5 database gates, 3 API gates, 1 startup policy)'
