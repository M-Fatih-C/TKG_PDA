import { screen, waitFor } from '@testing-library/react'
import { DashboardPage } from './DashboardPage'
import { jsonResponse, renderAuthenticated } from '../test/render'

describe('Dashboard navigation', () => {
  it('does not link normal users to deprecated layout or data-quality screens', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/rds/status')) {
        return Promise.resolve(jsonResponse({
          enabled: true,
          reachable: true,
          stale: false,
          lastSuccessfulRead: null,
          baseAddressDisplaySafe: 'http://rds.test',
          businessStatus: 200,
          businessMessage: 'Success',
          errorCode: null,
          errorMessage: null,
        }))
      }
      return Promise.resolve(jsonResponse([]))
    }))
    renderAuthenticated(<DashboardPage />)

    expect(await screen.findByText('Tanımlı Makine')).toBeVisible()
    expect(screen.getByText('Robot Filosu')).toBeVisible()
    expect(screen.getByText('Kritik Robot Alarmı')).toBeVisible()
    await waitFor(() => {
      const links = screen.getAllByRole('link').map((link) => link.getAttribute('href'))
      expect(links.some((href) => href?.includes('/layouts'))).toBe(false)
      expect(links.some((href) => href?.includes('/data-quality'))).toBe(false)
    })
    expect(screen.queryByText('Yayındaki Layout')).not.toBeInTheDocument()
    expect(screen.queryByText('Veri Kalite Konusu')).not.toBeInTheDocument()
    expect(screen.queryByText('Müdahale ve Veri Doğrulama')).not.toBeInTheDocument()
  })

  it('shows warning and critical robot states with text, not color alone', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/rds/status')) {
        return Promise.resolve(jsonResponse({
          enabled: true,
          reachable: true,
          stale: false,
          lastSuccessfulRead: null,
          baseAddressDisplaySafe: 'http://rds.test',
          businessStatus: 200,
          businessMessage: 'Success',
          errorCode: null,
          errorMessage: null,
        }))
      }
      if (url.endsWith('/rds/robots')) {
        return Promise.resolve(jsonResponse([
          { connectivity: 'ONLINE', execution: 'IDLE', motion: 'IDLE' },
          { connectivity: 'OFFLINE', execution: 'IDLE', motion: 'IDLE' },
        ]))
      }
      if (url.endsWith('/rds/alarms')) {
        return Promise.resolve(jsonResponse([
          { severity: 'fatal', robotId: 'sim_01', code: 'F1', description: 'Fatal' },
        ]))
      }
      return Promise.resolve(jsonResponse([]))
    }))
    renderAuthenticated(<DashboardPage />)

    expect(await screen.findByText('Dikkat')).toBeVisible()
    expect(screen.getByText('Kritik')).toBeVisible()
    expect(screen.getByRole('link', { name: /Robot Filosu/ })).toHaveClass('stat-card--warning')
    expect(screen.getByRole('link', { name: /Kritik Robot Alarmı/ })).toHaveClass('stat-card--danger')
  })

  it('keeps administration available when RDS telemetry is disabled', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/rds/status')) {
        return Promise.resolve(jsonResponse({ enabled: false, reachable: false, stale: false, lastSuccessfulRead: null, baseAddressDisplaySafe: null, businessStatus: null, businessMessage: null, errorCode: 'RDS_DISABLED', errorMessage: 'RDS kapalı.' }))
      }
      if (url.endsWith('/rds/robots') || url.endsWith('/rds/alarms')) {
        return Promise.resolve(jsonResponse({ title: 'Kullanılamıyor', detail: 'RDS kapalı.', code: 'RDS_DISABLED' }, { status: 503 }))
      }
      return Promise.resolve(jsonResponse([]))
    }))

    renderAuthenticated(<DashboardPage />)

    expect(await screen.findByRole('heading', { name: 'Genel Bakış' })).toBeVisible()
    expect(screen.getByText('Devre Dışı')).toBeVisible()
    expect(screen.getByText('Robot durumu okunamadı')).toBeVisible()
    expect(screen.getByText('Alarm bilgisi okunamadı')).toBeVisible()
  })
})
