-- TKG customer confirmation: "RE: AMR Takip ve Proje İlerleme".
-- The customer part code is preserved verbatim. A separate resolved code is
-- used only where the repository already has one unambiguous canonical part.
-- 66424BN100-10-HMTR has no confirmed canonical master row, so it remains
-- intentionally unresolved instead of being guessed as 66424BN000-10-HAOS.

BEGIN;

INSERT INTO master.container_types (code, name, category)
VALUES
    ('CLK_STR03', 'CLK_STR03', 'OTHER'),
    ('CLK_STR02', 'CLK_STR02', 'OTHER'),
    ('CLK_STR01', 'CLK_STR01', 'OTHER'),
    ('CLK_S963', 'CLK_S963', 'OTHER'),
    ('CLK_1284', 'CLK_1284', 'OTHER'),
    ('CLK_121213', 'CLK_121213', 'OTHER'),
    ('CLK_129486', 'CLK_129486', 'OTHER'),
    ('CLK_1286', 'CLK_1286', 'OTHER'),
    ('CLK_129413', 'CLK_129413', 'OTHER'),
    ('CLK_EXTN02', 'CLK_EXTN02', 'OTHER'),
    ('CLK_EXTN01', 'CLK_EXTN01', 'OTHER'),
    ('CLK_DB121012', 'CLK_DB121012', 'OTHER'),
    ('CLK_DB121010', 'CLK_DB121010', 'OTHER')
ON CONFLICT (code) DO UPDATE
SET name = EXCLUDED.name,
    is_active = true,
    updated_at = now();

WITH confirmed(customer_part_code, resolved_part_code, primary_code, alternative_code) AS (
    VALUES
        ('66424BN100-10-HMTR', NULL,                    'CLK_STR03',    'CLK_S963'),
        ('66424BN400-10-HMTR', '66424BN400-10-HMTR',   'CLK_STR02',    'CLK_S963'),
        ('66424GJ000-10-HAOS', '66424GJ000-10-HAOS',   'CLK_STR01',    'CLK_S963'),
        ('67115BN050-10-HMTR', '67115BN050-10-HAOS',   'CLK_1284',     'CLK_1284'),
        ('67121BN000-10-HMTR', '67121BN000-10-HAOS',   'CLK_1284',     'CLK_1284'),
        ('67121BN400-10-HMTR', '67121BN400-10-HMTR',   'CLK_121213',   'CLK_1284'),
        ('67121BN450-10-HMTR', '67121BN450-10-HMTR',   'CLK_121213',   'CLK_1284'),
        ('67121GJ000-10-HAOS', '67121GJ000-10-HAOS',   'CLK_121213',   'CLK_1284'),
        ('67121GJ050-10-HAOS', '67121GJ050-10-HAOS',   'CLK_129486',   'CLK_1284'),
        ('67131BN000-10-HMTR', '67131BN000-10-HAOS',   'CLK_1286',     'CLK_1284'),
        ('67131BN400-10-HMTR', '67131BN400-10-HMTR',   'CLK_129413',   'CLK_1284'),
        ('67133GJ000-10-HAOS', '67133GJ000-10-HAOS',   'CLK_129413',   'CLK_1284'),
        ('67133GJ050-10-HAOS', '67133GJ050-10-HAOS',   'CLK_129486',   'CLK_1284'),
        ('71550BN000-10-HMTR', '71550BN000-10-HAOS',   'CLK_EXTN02',   'CLK_S963'),
        ('71550BN400-10-HMTR', '71550BN400-10-HMTR',   'CLK_EXTN01',   'CLK_S963'),
        ('71560BN000-10-HMTR', '71560BN000-10-HAOS',   'CLK_EXTN02',   'CLK_S963'),
        ('71560BN400-10-HMTR', '71560BN400-10-HMTR',   'CLK_EXTN01',   'CLK_S963'),
        ('76150BN400-10-HWON', '76150BN400-10-HWON',   'CLK_DB121012', 'CLK_1284'),
        ('76150GJ000-10-HWON', '76150GJ000-10-HWON',   'CLK_DB121012', 'CLK_1284'),
        ('76160BN400-10-HWON', '76160BN400-10-HWON',   'CLK_DB121012', 'CLK_1284'),
        ('76160GJ000-10-HWON', '76160GJ000-10-HWON',   'CLK_DB121012', 'CLK_1284'),
        ('77150BN400-10-HWON', '77150BN400-10-HWON',   'CLK_DB121010', 'CLK_1284'),
        ('77160BN400-10-HWON', '77160BN400-10-HWON',   'CLK_DB121010', 'CLK_1284')
)
INSERT INTO master.finished_product_container_options (
    customer_part_code,
    part_id,
    primary_container_type_id,
    alternative_container_type_id,
    source_reference,
    is_active)
SELECT confirmed.customer_part_code,
       part.id,
       primary_container.id,
       alternative_container.id,
       'TKG_EMAIL_RE_AMR_TAKIP_V1',
       true
FROM confirmed
LEFT JOIN master.parts part ON part.part_code = confirmed.resolved_part_code
JOIN master.container_types primary_container ON primary_container.code = confirmed.primary_code
JOIN master.container_types alternative_container ON alternative_container.code = confirmed.alternative_code
ON CONFLICT (customer_part_code) DO UPDATE
SET part_id = EXCLUDED.part_id,
    primary_container_type_id = EXCLUDED.primary_container_type_id,
    alternative_container_type_id = EXCLUDED.alternative_container_type_id,
    source_reference = EXCLUDED.source_reference,
    is_active = true,
    updated_at = now();

COMMIT;
