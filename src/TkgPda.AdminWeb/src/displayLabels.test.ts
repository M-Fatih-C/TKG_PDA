import { auditActionLabel, auditEntityLabel, deviceTypeLabel, displayStatusLabel, operationLabel, roleLabel } from './displayLabels'

describe('Turkish administration labels', () => {
  it('translates common status, role and device codes without changing API values', () => {
    expect(displayStatusLabel('ONLINE')).toBe('Çevrimiçi')
    expect(displayStatusLabel('NOT READY')).toBe('Hazır Değil')
    expect(roleLabel('SUPERVISOR')).toBe('Operatör Lideri')
    expect(deviceTypeLabel('ADMIN_WEB')).toBe('Yönetim Paneli')
  })

  it('translates workflow and denetim labels while preserving unknown technical codes', () => {
    expect(operationLabel('BRING_FULL')).toBe('Dolu Malzeme Getir')
    expect(auditActionLabel('TRANSPORT_REQUEST_CREATED')).toBe('Taşıma talebi oluşturuldu')
    expect(auditEntityLabel('AUTH_SESSION')).toBe('Kullanıcı Oturumu')
    expect(displayStatusLabel('CUSTOM_VENDOR_STATE')).toBe('CUSTOM_VENDOR_STATE')
  })
})
