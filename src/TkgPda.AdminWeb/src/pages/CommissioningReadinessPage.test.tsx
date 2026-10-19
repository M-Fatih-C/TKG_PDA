import { screen } from '@testing-library/react'
import { CommissioningReadinessPage } from './CommissioningReadinessPage'
import { jsonResponse, renderAuthenticated } from '../test/render'

const row = {
  machineCode: 'SH-01-A',
  variantCode: '1',
  mapName: 'TKG_SH01A_V01_MAP',
  navigationCode: 'TKG_SH01A_V01_NAV',
  profileStatus: 'DRAFT',
  geometryVerified: false,
  activeTaskTemplates: 4,
  overallState: 'NOT_READY',
  ready: false,
  operations: [
    { operation: 'BRING_FULL', state: 'BLOCKED', blockers: ['PREPARATION_WORKSITE_MAPPING_MISSING'] },
    { operation: 'BRING_EMPTY', state: 'BLOCKED', blockers: ['EMPTY_SOURCE_MAPPING_MISSING'] },
    { operation: 'REMOVE_FULL_MAMUL', state: 'BLOCKED', blockers: ['FINISHED_GOODS_MAPPING_MISSING'] },
    { operation: 'REMOVE_FULL_ALT_RETURN', state: 'BLOCKED', blockers: ['NON_FINISHED_RETURN_MAPPING_MISSING'] },
    { operation: 'REMOVE_EMPTY', state: 'BLOCKED', blockers: ['EMPTY_RETURN_MAPPING_MISSING'] },
  ],
}

describe('Commissioning readiness', () => {
  it('shows Turkish blockers and does not call a blocked operation ready', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/rds/commissioning/report')) return Promise.resolve(jsonResponse([row]))
      return Promise.resolve(jsonResponse([]))
    }))

    renderAuthenticated(<CommissioningReadinessPage />)

    expect(await screen.findByText('SH-01-A')).toBeVisible()
    expect(screen.getByText('Malzeme Getir')).toBeVisible()
    expect(screen.getByText('⚠ Hazırlık alanı bağlı değil')).toBeVisible()
    expect(screen.getByText('⚠ Boş kasa kaynağı bağlı değil')).toBeVisible()
    expect(screen.getByText('Mamul Kasası Götür')).toBeVisible()
    expect(screen.getByText('Alt / Yarı Mamul Dönüşü')).toBeVisible()
    expect(screen.getByText('⚠ Bitmiş ürün depo alanı bağlı değil')).toBeVisible()
    expect(screen.getByText('⚠ Alt dönüş alanı eksik')).toBeVisible()
    expect(screen.getByText('⚠ Boş kasa dönüş alanı eksik')).toBeVisible()
    expect(screen.queryByText('✓ Hazır')).not.toBeInTheDocument()
  })
})
