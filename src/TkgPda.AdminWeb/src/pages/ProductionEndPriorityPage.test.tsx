import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ProductionEndPriorityPage } from './ProductionEndPriorityPage'
import { jsonResponse, renderAuthenticated } from '../test/render'

const firstRow = {
  machineId: 'machine-1', machineCode: 'SH-01-A', machineName: 'Şase Hattı 01 A',
  variantId: 'variant-1', variantCode: 'V1', variantName: '1. Varyant',
  productGroupId: 'group-1', mainPartCode: 'MAIN-100',
  layoutId: 'layout-1', layoutCode: 'LAYOUT-1', layoutName: 'Hat Yerleşimi',
  layoutSlotId: 'slot-1', slotCode: 'SLOT-01', slotName: 'Palet Alanı 1',
  partId: 'part-1', partCode: 'ALT-200', partRole: 'ALT_PART',
  priority: null, updatedAt: null,
}
const secondRow = {
  ...firstRow,
  layoutSlotId: 'slot-2', slotCode: 'SLOT-02', slotName: 'Palet Alanı 2',
  partId: 'part-2', partCode: 'MAIN-100', partRole: 'MAIN_PART',
}

describe('ProductionEndPriorityPage', () => {
  it('presents a visual area sequence and saves every exact mapping atomically', async () => {
    const rows = [firstRow, secondRow]
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/production-end-area-priorities/batch') && init?.method === 'PUT') {
        return Promise.resolve(jsonResponse(rows))
      }
      if (url.endsWith('/production-end-area-priorities')) return Promise.resolve(jsonResponse(rows))
      if (url.endsWith('/production-end-priorities')) return Promise.resolve(jsonResponse([
        { operationCode: 'REMOVE_FULL', priority: 40, updatedAt: null },
        { operationCode: 'REMOVE_EMPTY', priority: 70, updatedAt: null },
      ]))
      return Promise.resolve(jsonResponse([]))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderAuthenticated(<ProductionEndPriorityPage />)

    expect(await screen.findByRole('article', { name: '1. Palet Alanı 1' })).toBeVisible()
    expect(screen.getByRole('article', { name: '2. Palet Alanı 2' })).toBeVisible()
    expect(screen.getByText('Dolu palet: 40 · Boş palet: 70')).toBeVisible()

    await userEvent.click(screen.getByRole('button', { name: 'Palet Alanı 2 alanını yukarı taşı' }))
    await userEvent.click(screen.getByRole('button', { name: 'Sıralamayı kaydet' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/production-end-area-priorities/batch'),
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ items: [
          { productGroupId: 'group-1', layoutSlotId: 'slot-2', partId: 'part-2', priority: 10 },
          { productGroupId: 'group-1', layoutSlotId: 'slot-1', partId: 'part-1', priority: 20 },
        ] }),
      }),
    ))
  })

  it('keeps exact part overrides in the advanced section', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/production-end-area-priorities/batch') && init?.method === 'PUT') return Promise.resolve(jsonResponse([{ ...firstRow, priority: 7 }]))
      if (url.endsWith('/production-end-area-priorities')) return Promise.resolve(jsonResponse([firstRow]))
      if (url.endsWith('/production-end-priorities')) return Promise.resolve(jsonResponse([]))
      return Promise.resolve(jsonResponse([]))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderAuthenticated(<ProductionEndPriorityPage />)

    await userEvent.click(await screen.findByText('Gelişmiş: parçaya özel sıra tanımları'))
    const input = screen.getByLabelText('ALT-200 SLOT-01 parçaya özel sırası')
    await userEvent.type(input, '7')
    await userEvent.click(screen.getAllByRole('button', { name: 'Kaydet' })[0])

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/production-end-area-priorities/batch'),
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ items: [{ productGroupId: 'group-1', layoutSlotId: 'slot-1', partId: 'part-1', priority: 7 }] }) }),
    ))
  })
})
