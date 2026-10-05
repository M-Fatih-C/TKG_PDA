import type { AdminSession, AdminUser, ContainerType, DataIssue, Device, Layout, Machine, Role, Slot, Variant } from '../api/types'

export const session: AdminSession = {
  accessToken: 'test-admin-jwt', tokenType: 'Bearer', expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(), clientType: 'ADMIN_WEB',
  user: { id: 'u-admin', username: 'admin', fullName: 'Fatih Yönetici', employeeNo: 'A-001', roles: ['ADMIN'] },
}

export const roles: Role[] = [
  { id: 'r-admin', code: 'ADMIN', name: 'Administrator', isSystem: true, isActive: true },
  { id: 'r-supervisor', code: 'SUPERVISOR', name: 'Supervisor', isSystem: true, isActive: true },
  { id: 'r-line', code: 'LINE_OPERATOR', name: 'Line Operator', isSystem: true, isActive: true },
  { id: 'r-warehouse', code: 'WAREHOUSE_OPERATOR', name: 'Warehouse Operator', isSystem: true, isActive: true },
  { id: 'r-semi-finished', code: 'SEMI_FINISHED_WAREHOUSE_OPERATOR', name: 'Yarı Mamul Depo Operatörü', isSystem: true, isActive: true },
  { id: 'r-finished-goods', code: 'FINISHED_GOODS_WAREHOUSE_OPERATOR', name: 'Bitmiş Ürün Depo Operatörü', isSystem: true, isActive: true },
]

export const machines: Machine[] = [{ id: 'm-1', code: 'SH-01-A', displayName: 'Şase Hattı 01 A', displayOrder: 1, isActive: true, concurrencyToken: 41 }]
export const users: AdminUser[] = [
  { ...session.user, isActive: true, lastLoginAt: '2026-08-18T12:00:00Z', machinePermissions: [], concurrencyToken: 51 },
  { id: 'u-line', username: 'operator', fullName: 'Hat Operatörü', employeeNo: 'L-101', roles: ['LINE_OPERATOR'], isActive: true, lastLoginAt: null, machinePermissions: [{ machineId: 'm-1', canView: true, canOperate: true, canChangeVariant: false, canManageSlots: false }], concurrencyToken: 52 },
]
export const devices: Device[] = [{ id: 'd-1', code: 'PDA-01', name: 'Saha PDA 01', type: 'PDA', isActive: true, metadata: { area: 'SH' }, concurrencyToken: 61 }]
export const variants: Variant[] = [{ id: 'v-1', machineId: 'm-1', code: 'V1', name: 'Varyant 1', displayOrder: 1, isActive: true, defaultLayoutId: 'l-1', concurrencyToken: 71 }]
export const layouts: Layout[] = [{ id: 'l-1', machineId: 'm-1', code: 'SH01-LAYOUT', name: 'Müşteri Fiziksel Planı', version: 2, status: 'DRAFT', slotCount: 2, assignedVariantCount: 1, publishedAt: null, concurrencyToken: 81 }]
export const containers: ContainerType[] = [{ id: 'c-1', code: '1284', name: 'Kasa 1284', category: 'BOX', lengthMm: null, widthMm: null, heightMm: null, capacityQuantity: null, isActive: true, concurrencyToken: 91 }]
export const slots: Slot[] = [
  { id: 's-1', layoutId: 'l-1', code: 'SLOT-01', displayName: 'Boş Kasa', role: 'DROP', defaultContainerTypeId: 'c-1', x: .1, y: .14, width: .2, height: .16, displayOrder: 1, allowsEmptyLoad: true, allowsFullLoad: false, isEnabled: true, metadata: {}, concurrencyToken: 101 },
  { id: 's-2', layoutId: 'l-1', code: 'SLOT-02', displayName: 'Dolu Kasa', role: 'PICKUP', defaultContainerTypeId: 'c-1', x: .55, y: .58, width: .2, height: .16, displayOrder: 2, allowsEmptyLoad: false, allowsFullLoad: true, isEnabled: true, metadata: {}, concurrencyToken: 102 },
]
export const issues: DataIssue[] = [{ kind: 'UNKNOWN_CONTAINER_FOOTPRINT', severity: 'MEDIUM', entityType: 'CONTAINER_TYPE', entityId: 'c-1', code: '1284', message: 'One or more physical footprint dimensions are unknown and remain NULL.' }]
