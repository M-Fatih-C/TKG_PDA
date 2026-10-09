import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { DevicesPage } from './DevicesPage'
import { devices, machines } from '../test/fixtures'
import { jsonResponse, renderAuthenticated } from '../test/render'

const mockDevicesApi = () => vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
  const url = String(input)
  if (url.endsWith('/devices')) return Promise.resolve(jsonResponse(devices))
  if (url.endsWith('/machines')) return Promise.resolve(jsonResponse(machines))
  return Promise.resolve(jsonResponse([]))
}))

describe('DevicesPage machine binding', () => {
  it('displays machine binding information for bound devices', async () => {
    mockDevicesApi()
    renderAuthenticated(<DevicesPage />)
    expect(await screen.findByText('PDA-01')).toBeVisible()
    expect(screen.getByText('SH-01-A')).toBeVisible()
    expect(screen.getByText('Şase Hattı 01 A')).toBeVisible()
  })

  it('allows opening create device dialog with machine binding options from API', async () => {
    mockDevicesApi()
    renderAuthenticated(<DevicesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Cihaz ekle/ }))

    expect(screen.getByText('Yeni cihaz')).toBeVisible()
    expect(screen.getByLabelText(/Makine Bağlantısı/)).toBeVisible()
    expect(screen.getByRole('option', { name: /Bağlı Değil \/ Depo Tableti/ })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: /SH-01-A — Şase Hattı 01 A/ })).toBeInTheDocument()
  })

  it('allows opening edit device dialog and selecting unbound', async () => {
    mockDevicesApi()
    renderAuthenticated(<DevicesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Düzenle/ }))

    expect(screen.getByText('PDA-01 düzenle')).toBeVisible()
    const select = screen.getByLabelText(/Makine Bağlantısı/) as HTMLSelectElement
    expect(select.value).toBe('m-1')

    await userEvent.selectOptions(select, '')
    expect(select.value).toBe('')
  })
})
