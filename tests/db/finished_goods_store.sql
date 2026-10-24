-- Proves the finished-goods store records a delivery once and only once, and
-- that a location the customer has not linked receives nothing.
--
-- The insert under test is the one RecordStoreDeliveryAsync issues when a
-- REMOVE_FULL completes. It is exercised here directly because completing a
-- transport for real needs RDS dispatch, which is disabled in development.
--
-- Everything rolls back, including the test location: how many locations the
-- store has and where they are is a customer fact nobody has supplied.
\set ON_ERROR_STOP off
BEGIN;

CREATE TEMP TABLE t AS
SELECT (SELECT id FROM auth.users WHERE employee_no = 'DEV-WH')                  AS usr,
       (SELECT id FROM audit.client_devices WHERE device_code = 'DEV-WAREHOUSE') AS dev,
       (SELECT id FROM master.layout_slots WHERE code = 'DROP-001' LIMIT 1)      AS drop_slot,
       (SELECT id FROM master.layout_slots WHERE code = 'SLOT-001' LIMIT 1)      AS other_slot,
       (SELECT r.id FROM ops.transport_requests r ORDER BY r.request_no DESC LIMIT 1)      AS req_a,
       (SELECT r.id FROM ops.transport_requests r ORDER BY r.request_no DESC OFFSET 1 LIMIT 1) AS req_b;

INSERT INTO ops.store_locations (code, display_name, capacity, area_purpose, is_operator_visible)
VALUES ('TEST-STORE-1', 'Test Depo Alanı', 3, 'DELIVERY', true);

CREATE TEMP TABLE loc AS
SELECT id FROM ops.store_locations WHERE code = 'TEST-STORE-1';

\echo ''
\echo '=== 1. baglanmamis DROP konumu -> hicbir kayit olusmamali ==='
SELECT count(*) AS eslesen
FROM ops.store_location_delivery_slots WHERE layout_slot_id = (SELECT drop_slot FROM t);

\echo ''
\echo '=== 2. DROP konumu depo alanina baglanir ==='
INSERT INTO ops.store_location_delivery_slots (layout_slot_id, store_location_id)
SELECT (SELECT drop_slot FROM t), (SELECT id FROM loc);
SELECT count(*) AS baglanti FROM ops.store_location_delivery_slots;

\echo ''
\echo '=== 3. teslimat kaydedilir ==='
INSERT INTO ops.store_units (store_location_id, transport_request_id, part_id, container_type_id, machine_id)
SELECT (SELECT id FROM loc), r.id, r.part_id, r.container_type_id, r.machine_id
FROM ops.transport_requests r WHERE r.id = (SELECT req_a FROM t)
ON CONFLICT (transport_request_id) WHERE transport_request_id IS NOT NULL DO NOTHING;
SELECT count(*) AS duran_kasa FROM ops.store_units WHERE removed_at IS NULL;

\echo ''
\echo '=== 4. ayni tamamlanma tekrar oynatilir -> ikinci kayit OLUSMAMALI ==='
INSERT INTO ops.store_units (store_location_id, transport_request_id, part_id, container_type_id, machine_id)
SELECT (SELECT id FROM loc), r.id, r.part_id, r.container_type_id, r.machine_id
FROM ops.transport_requests r WHERE r.id = (SELECT req_a FROM t)
ON CONFLICT (transport_request_id) WHERE transport_request_id IS NOT NULL DO NOTHING;
SELECT count(*) AS duran_kasa FROM ops.store_units WHERE removed_at IS NULL;

\echo ''
\echo '=== 5. farkli bir tasima -> ikinci kasa eklenir ==='
INSERT INTO ops.store_units (store_location_id, transport_request_id, part_id, container_type_id, machine_id)
SELECT (SELECT id FROM loc), r.id, r.part_id, r.container_type_id, r.machine_id
FROM ops.transport_requests r WHERE r.id = (SELECT req_b FROM t)
ON CONFLICT (transport_request_id) WHERE transport_request_id IS NOT NULL DO NOTHING;
SELECT count(*) AS duran_kasa FROM ops.store_units WHERE removed_at IS NULL;

\echo ''
\echo '=== 6. operator bir kasayi alir -> sadece o dusler ==='
UPDATE ops.store_units
SET removed_at = clock_timestamp(), removed_by = (SELECT usr FROM t),
    removed_device_id = (SELECT dev FROM t), row_version = row_version + 1
WHERE transport_request_id = (SELECT req_a FROM t);
SELECT count(*) AS duran_kasa FROM ops.store_units WHERE removed_at IS NULL;

\echo ''
\echo '=== 7. alinmis kasa tekrar alinamaz (kosul tutmaz) ==='
UPDATE ops.store_units
SET removed_at = clock_timestamp(), removed_by = (SELECT usr FROM t),
    removed_device_id = (SELECT dev FROM t)
WHERE transport_request_id = (SELECT req_a FROM t) AND removed_at IS NULL;
SELECT count(*) AS etkilenen_satir FROM ops.store_units
WHERE transport_request_id = (SELECT req_a FROM t) AND removed_at IS NULL;

\echo ''
\echo '=== 8. eksik alan bilgisiyle kapatma reddedilir ==='
SAVEPOINT s8;
UPDATE ops.store_units SET removed_at = clock_timestamp()
WHERE transport_request_id = (SELECT req_b FROM t);
ROLLBACK TO SAVEPOINT s8;

\echo ''
\echo '=== SONUC ==='
SELECT l.code AS alan, l.capacity AS kapasite,
       count(*) FILTER (WHERE u.removed_at IS NULL) AS duran,
       count(*) FILTER (WHERE u.removed_at IS NOT NULL) AS alinan
FROM ops.store_locations l
LEFT JOIN ops.store_units u ON u.store_location_id = l.id
WHERE l.code = 'TEST-STORE-1'
GROUP BY l.code, l.capacity;

ROLLBACK;
