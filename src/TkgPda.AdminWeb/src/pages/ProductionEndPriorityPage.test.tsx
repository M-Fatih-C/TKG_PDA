import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ProductionEndPriorityPage } from './ProductionEndPriorityPage'
import { jsonResponse, renderAuthenticated } from '../test/render'

const row = {
  machineId: 'machine-1', machineCode: 'SH-01-A', machineName: 'Şase Hattı 01 A',
  variantId: 'variant-1', variantCode: 'V1', variantName: '1. Varyant',
  productGroupId: 'group-1', mainPartCode: 'MAIN-100',
  layoutId: 'layout-1', layoutCode: 'LAYOUT-1', layoutName: 'Hat Yerleşimi',
  layoutSlotId: 'slot-1', slotCode: 'SLOT-01', slotName: 'Palet Alanı 1',
  partId: 'part-1', partCode: 'ALT-200', partRole: 'ALT_PART',
  priority: null, updatedAt: null,
}

describe('ProductionEndPriorityPage', () => {
  it('shows area-based customer context and saves the exact mapping', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/production-end-area-priorities') && init?.method === 'PUT') {
        return Promise.resolve(jsonResponse([{ ...row, priority: 7 }]))
      }
      if (url.endsWith('/production-end-area-priorities')) return Promise.resolve(jsonResponse([row]))
      return Promise.resolve(jsonResponse([]))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderAuthenticated(<ProductionEndPriorityPage />)

    expect(await screen.findByText('Palet Alanı 1')).toBeVisible()
    expect(screen.getByText('Alternatif parça')).toBeVisible()
    expect(screen.getByText('MAIN-100')).toBeVisible()
    const priority = screen.getByLabelText(/SH-01-A V1 ALT-200 SLOT-01 önceliği/)
    await userEvent.type(priority, '7')
    await userEvent.click(screen.getByRole('button', { name: 'Kaydet' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/production-end-area-priorities'),
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ productGroupId: 'group-1', layoutSlotId: 'slot-1', partId: 'part-1', priority: 7 }) }),
    ))
  })
})
