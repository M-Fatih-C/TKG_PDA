import type { RoleCode } from './api/types'

const statusLabels: Record<string, string> = {
  ACTIVE: 'Aktif',
  INACTIVE: 'Pasif',
  PUBLISHED: 'Yayında',
  DRAFT: 'Taslak',
  ARCHIVED: 'Arşivlendi',
  LOW: 'Düşük',
  MEDIUM: 'Orta',
  HIGH: 'Yüksek',
  CONNECTED: 'Bağlı',
  UNAVAILABLE: 'Erişilemiyor',
  DISABLED: 'Devre Dışı',
  ONLINE: 'Çevrimiçi',
  OFFLINE: 'Çevrimdışı',
  UNKNOWN: 'Bilinmiyor',
  DISPATCHABLE: 'Sevke Uygun',
  UNDISPATCHABLE: 'Sevke Uygun Değil',
  MOVING: 'Hareket Ediyor',
  BLOCKED: 'Engellendi',
  PAUSED: 'Duraklatıldı',
  IDLE: 'Bekliyor',
  RUNNING: 'Çalışıyor',
  ERROR: 'Hata',
  SUSPENDED: 'Askıda',
  FINISHED: 'Tamamlandı',
  TKG_CORRELATED: 'TKG ile İlişkili',
  EXTERNAL_OR_OTHER: 'Harici / Diğer',
  UNLOCKED: 'Kilitli Değil',
  LOCKED: 'Kilitli',
  READY: 'Hazır',
  'NOT READY': 'Hazır Değil',
  PHYSICAL: 'Fiziksel',
  LOGICAL: 'Mantıksal',
  FATAL: 'Kritik',
  WARNING: 'Uyarı',
  NOTICE: 'Bilgi',
  YES: 'Evet',
  NO: 'Hayır',
  ADMIN: 'Yönetici',
  SUPERVISOR: 'Operatör Lideri',
  LINE_OPERATOR: 'Hat Operatörü',
  WAREHOUSE_OPERATOR: 'Depo Operatörü',
  SEMI_FINISHED_WAREHOUSE_OPERATOR: 'Yarı Mamul Depo Operatörü',
  FINISHED_GOODS_WAREHOUSE_OPERATOR: 'Bitmiş Ürün Depo Operatörü',
  ADMIN_WEB: 'Yönetim Paneli',
  SERVICE: 'Servis',
  OTHER: 'Diğer',
}

const operationLabels: Record<string, string> = {
  BRING_EMPTY: 'Boş Kasa Getir',
  BRING_FULL: 'Dolu Malzeme Getir',
  REMOVE_FULL: 'Dolu Kasa Götür',
  REMOVE_EMPTY: 'Boş Kasa Götür',
}

const entityLabels: Record<string, string> = {
  TRANSPORT_REQUEST: 'Taşıma Talebi',
  TRANSPORT_BATCH: 'Toplu Taşıma Talebi',
  AUTH_SESSION: 'Kullanıcı Oturumu',
  AUTH_USER: 'Kullanıcı Hesabı',
  DEVICE: 'Cihaz',
  MACHINE: 'Makine',
  VARIANT: 'Varyant',
  LAYOUT: 'Yerleşim Planı',
  RDSCORE_DISCOVERY_SNAPSHOT: 'RDS Keşif Kaydı',
}

const actionLabels: Record<string, string> = {
  ADMIN_WEB_LOGIN_SUCCEEDED: 'Yönetim paneli girişi başarılı',
  ADMIN_WEB_LOGIN_FAILED: 'Yönetim paneli girişi başarısız',
  AUTH_LOGIN_SUCCEEDED: 'Kullanıcı girişi başarılı',
  AUTH_LOGIN_FAILED: 'Kullanıcı girişi başarısız',
  AUTH_LOGIN_BLOCKED_ACTIVE_SESSION: 'Etkin oturum nedeniyle giriş engellendi',
  AUTH_SESSION_CREATED: 'Kullanıcı oturumu açıldı',
  AUTH_SESSION_LOGOUT: 'Kullanıcı oturumu kapatıldı',
  USER_LOGOUT: 'Kullanıcı çıkış yaptı',
  USER_CREATED: 'Kullanıcı oluşturuldu',
  USER_UPDATED: 'Kullanıcı güncellendi',
  USER_PASSWORD_RESET: 'Kullanıcı parolası yenilendi',
  USER_ROLES_UPDATED: 'Kullanıcı rolleri güncellendi',
  MACHINE_PERMISSION_UPDATED: 'Makine yetkisi güncellendi',
  MACHINE_PERMISSION_REMOVED: 'Makine yetkisi kaldırıldı',
  DEVICE_CREATED: 'Cihaz oluşturuldu',
  DEVICE_UPDATED: 'Cihaz güncellendi',
  MACHINE_UPDATED: 'Makine güncellendi',
  VARIANT_CREATED: 'Varyant oluşturuldu',
  VARIANT_UPDATED: 'Varyant güncellendi',
  REQUEST_CREATED: 'Talep oluşturuldu',
  TRANSPORT_REQUEST_CREATED: 'Taşıma talebi oluşturuldu',
  TRANSPORT_REQUEST_CANCELLED: 'Taşıma talebi iptal edildi',
  TRANSPORT_REQUEST_MARKED_READY: 'Taşıma talebi hazırlandı',
  TRANSPORT_BATCH_CREATED: 'Toplu taşıma talebi oluşturuldu',
  RDS_COMMISSIONING_DISCOVERY_CAPTURED: 'RDS keşif kaydı alındı',
}

const fallbackTokens: Record<string, string> = {
  ADMIN: 'Yönetim',
  WEB: 'Paneli',
  USER: 'Kullanıcı',
  AUTH: 'Kimlik',
  LOGIN: 'Giriş',
  LOGOUT: 'Çıkış',
  SESSION: 'Oturum',
  REQUEST: 'Talep',
  TRANSPORT: 'Taşıma',
  BATCH: 'Toplu',
  RDS: 'RDS',
  ROBOT: 'Robot',
  MACHINE: 'Makine',
  VARIANT: 'Varyant',
  DEVICE: 'Cihaz',
  PERMISSION: 'Yetki',
  ROLE: 'Rol',
  CREATED: 'Oluşturuldu',
  UPDATED: 'Güncellendi',
  REMOVED: 'Kaldırıldı',
  CANCELLED: 'İptal Edildi',
  READY: 'Hazırlandı',
  SUCCEEDED: 'Başarılı',
  FAILED: 'Başarısız',
  BLOCKED: 'Engellendi',
  STARTED: 'Başlatıldı',
  COMPLETED: 'Tamamlandı',
}

export const displayStatusLabel = (value: string) => statusLabels[value] ?? value

export const roleLabel = (code: RoleCode, fallback?: string) => statusLabels[code] ?? fallback ?? code

export const deviceTypeLabel = (value: string) => statusLabels[value] ?? value

export const operationLabel = (value: string) => operationLabels[value] ?? value

export const auditEntityLabel = (value: string) => entityLabels[value] ?? value

export function auditActionLabel(value: string) {
  const direct = actionLabels[value]
  if (direct) return direct

  const withoutAdminPrefix = value.startsWith('ADMIN_') ? value.slice('ADMIN_'.length) : value
  const normalized = actionLabels[withoutAdminPrefix]
  if (normalized) return normalized

  return withoutAdminPrefix
    .split('_')
    .map((token) => fallbackTokens[token] ?? token)
    .join(' ')
}
