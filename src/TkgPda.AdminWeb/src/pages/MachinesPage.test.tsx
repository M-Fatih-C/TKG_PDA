import { screen } from '@testing-library/react'
import { MachinesPage } from './MachinesPage'
import { jsonResponse, renderAuthenticated } from '../test/render'
import { layouts, machines, variants } from '../test/fixtures'

describe('Machine and variant management', () => {
  it('renders the selected machine, variants and assigned default layout', async () => {
    const publishedLayouts = layouts.map((layout) => ({ ...layout, status: 'PUBLISHED' as const }))
    vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/machines')) return Promise.resolve(jsonResponse(machines))
      if (url.includes('/variants')) return Promise.resolve(jsonResponse(variants))
      if (url.includes('/layouts')) return Promise.resolve(jsonResponse(publishedLayouts))
      return Promise.resolve(jsonResponse([]))
    }))

    renderAuthenticated(<MachinesPage />)

    expect(await screen.findByRole('heading', { name: /SH-01-A/ })).toBeVisible()
    expect(screen.getByText('Varyant 1')).toBeVisible()
    expect(screen.getByRole('combobox', { name: /varsayılan yerleşim planı/i })).toHaveValue('l-1')
  })
})
