-- Migration 011: Warehouse Operator Role Split
-- Forward-only addition after 010.
--
-- WHAT THIS ADDS
-- --------------
-- 1. SEMI_FINISHED_WAREHOUSE_OPERATOR ('Yarı Mamul Depo Operatörü')
-- 2. FINISHED_GOODS_WAREHOUSE_OPERATOR ('Bitmiş Ürün Depo Operatörü')
--
-- BACKWARD COMPATIBILITY
-- ----------------------
-- WAREHOUSE_OPERATOR is preserved as a legacy/transitional compatibility role.
-- No existing roles, users, or user-role assignments are modified or deleted.

BEGIN;

INSERT INTO auth.roles (code, name, is_system) VALUES
    ('SEMI_FINISHED_WAREHOUSE_OPERATOR', 'Yarı Mamul Depo Operatörü', true),
    ('FINISHED_GOODS_WAREHOUSE_OPERATOR', 'Bitmiş Ürün Depo Operatörü', true)
ON CONFLICT (code) DO NOTHING;

COMMIT;
