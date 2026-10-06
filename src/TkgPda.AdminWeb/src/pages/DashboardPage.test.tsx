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
})
