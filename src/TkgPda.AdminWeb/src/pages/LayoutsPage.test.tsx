import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { LayoutsPage } from './LayoutsPage'
import { layouts, machines, slots } from '../test/fixtures'
import { jsonResponse, renderAuthenticated } from '../test/render'

describe('LayoutsPage lifecycle and pre-publish validation gate', () => {
  it('blocks publication when critical geometry or routing checks fail', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/layouts')) return Promise.resolve(jsonResponse(layouts))
      if (url.endsWith('/machines')) return Promise.resolve(jsonResponse(machines))
      // Return slots where SLOT-01 has NULL geometry
      if (/\/layouts\/[^/]+\/slots$/.test(url)) {
        return Promise.resolve(
          jsonResponse([
            { ...slots[0], x: null, y: null, width: null, height: null },
            slots[1],
          ]),
        )
      }
      if (url.includes('/parts')) return Promise.resolve(jsonResponse([]))
      if (url.includes('/rds/slot-mappings')) return Promise.resolve(jsonResponse([]))
      return Promise.resolve(jsonResponse([]))
    })
    vi.stubGlobal('fetch', fetchMock)

    renderAuthenticated(
      <Routes>
        <Route path="/layouts" element={<LayoutsPage />} />
      </Routes>,
      '/layouts',
    )

    // Open publish dialog for the draft layout
    const publishButton = await screen.findByRole('button', { name: /Yayınla/ })
    await userEvent.click(publishButton)

    const dialog = await screen.findByRole('dialog', { name: /Layout sürümünü yayınla/ })
    expect(dialog).toBeVisible()

    // Expect critical preflight error for unplaced geometry
    expect(await within(dialog).findByText(/Yayın Öncesi Kritik Hatalar/)).toBeVisible()
    expect(within(dialog).getByText(/Geometrisi eksik veya ekran sınırları dışına taşan/)).toBeVisible()

    // Confirm the dialog's confirm button is disabled
    const confirmButton = within(dialog).getByRole('button', { name: 'Yayınla' })
    expect(confirmButton).toBeDisabled()
  })

  it('allows publication when preflight validation passes', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST' && url.includes('/publish')) {
        return Promise.resolve(jsonResponse({ ...layouts[0], status: 'PUBLISHED' }))
      }
      if (url.endsWith('/layouts')) return Promise.resolve(jsonResponse(layouts))
      if (url.endsWith('/machines')) return Promise.resolve(jsonResponse(machines))
      if (/\/layouts\/[^/]+\/slots$/.test(url)) return Promise.resolve(jsonResponse(slots))
      // Both slots have parts assigned
      if (url.includes('/parts')) {
        return Promise.resolve(jsonResponse([{ partId: 'p-1', partCode: '71550BN000', description: 'Parça' }]))
      }
      // Both slots have RDS WorkSite mapped
      if (url.includes('/rds/slot-mappings')) {
        return Promise.resolve(
          jsonResponse([
            { slotId: 's-1', rdsWorkSiteId: 'ws-1', rdsSiteId: 'SITE-01', executionReady: true },
            { slotId: 's-2', rdsWorkSiteId: 'ws-2', rdsSiteId: 'SITE-02', executionReady: true },
          ]),
        )
      }
      return Promise.resolve(jsonResponse([]))
    })
    vi.stubGlobal('fetch', fetchMock)

    renderAuthenticated(
      <Routes>
        <Route path="/layouts" element={<LayoutsPage />} />
      </Routes>,
      '/layouts',
    )

    const publishButton = await screen.findByRole('button', { name: /Yayınla/ })
    await userEvent.click(publishButton)

    const dialog = await screen.findByRole('dialog', { name: /Layout sürümünü yayınla/ })
    expect(dialog).toBeVisible()

    // Preflight success banner should appear
    expect(await within(dialog).findByText(/Tüm preflight doğrulamaları başarılı/)).toBeVisible()

    const confirmButton = within(dialog).getByRole('button', { name: 'Yayınla' })
    expect(confirmButton).not.toBeDisabled()

    await userEvent.click(confirmButton)
    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'POST')).toBe(true)
    })
  })

  it('treats OTHER slot role as operational logistics location and validates its routing', async () => {
    // A layout whose operational slot is role OTHER (e.g. customer operational logistics buffer/station)
    const otherSlots = [
      { ...slots[0], role: 'OTHER' as const, code: 'SLOT-LOGISTICS-OTHER' },
    ]
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/layouts')) return Promise.resolve(jsonResponse(layouts))
      if (url.endsWith('/machines')) return Promise.resolve(jsonResponse(machines))
      if (/\/layouts\/[^/]+\/slots$/.test(url)) return Promise.resolve(jsonResponse(otherSlots))
      // Empty parts -> OTHER slot missing routing
      if (url.includes('/parts')) return Promise.resolve(jsonResponse([]))
      if (url.includes('/rds/slot-mappings')) return Promise.resolve(jsonResponse([]))
      return Promise.resolve(jsonResponse([]))
    })
    vi.stubGlobal('fetch', fetchMock)

    renderAuthenticated(
      <Routes>
        <Route path="/layouts" element={<LayoutsPage />} />
      </Routes>,
      '/layouts',
    )

    const publishButton = await screen.findByRole('button', { name: /Yayınla/ })
    await userEvent.click(publishButton)

    const dialog = await screen.findByRole('dialog', { name: /Layout sürümünü yayınla/ })
    expect(dialog).toBeVisible()

    // Should recognize the OTHER slot as operational, and fail because it has no parts assigned
    expect(await within(dialog).findByText(/Parça eşlemesi olmayan 1 operasyonel slot var/)).toBeVisible()
    expect(within(dialog).getByText(/SLOT-LOGISTICS-OTHER/)).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Yayınla' })).toBeDisabled()
  })
})
