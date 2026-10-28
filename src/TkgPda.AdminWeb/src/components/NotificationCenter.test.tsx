import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { jsonResponse, renderAuthenticated } from '../test/render'
import { NotificationCenter } from './NotificationCenter'

describe('NotificationCenter', () => {
  it('groups repeated data issues and presents one Turkish notification', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/admin/data-issues')) {
        return Promise.resolve(jsonResponse([
          {
            kind: 'UNRESOLVED_SLOT_CONTAINER',
            severity: 'HIGH',
            entityType: 'LAYOUT_SLOT',
            entityId: 'slot-a',
            code: 'SLOT-001',
            message: 'Kaynak yerleşimde bu alan için doğrulanmış kasa veya palet eşleştirmesi bulunmuyor.',
          },
          {
            kind: 'UNRESOLVED_SLOT_CONTAINER',
            severity: 'HIGH',
            entityType: 'LAYOUT_SLOT',
            entityId: 'slot-b',
            code: 'SLOT-001',
            message: 'Kaynak yerleşimde bu alan için doğrulanmış kasa veya palet eşleştirmesi bulunmuyor.',
          },
        ]))
      }
      if (url.endsWith('/rds/status')) {
        return Promise.resolve(jsonResponse({
          enabled: true,
          reachable: true,
          errorCode: null,
          errorMessage: null,
        }))
      }
      return Promise.resolve(jsonResponse([]))
    }))

    renderAuthenticated(<NotificationCenter />)

    await userEvent.click(await screen.findByRole('button', { name: '1 okunmamış bildirim' }))
    expect(screen.getByText('1 aktif durum · 1 okunmamış')).toBeVisible()
    expect(screen.getByText('Veri Kalite Uyarısı: SLOT-001 (2 kayıt)')).toBeVisible()
    expect(screen.getByText(/Aynı durum 2 kayıtta bulunuyor/)).toBeVisible()
    expect(screen.queryAllByText(/Veri Kalite Uyarısı: SLOT-001/)).toHaveLength(1)
  })
})
