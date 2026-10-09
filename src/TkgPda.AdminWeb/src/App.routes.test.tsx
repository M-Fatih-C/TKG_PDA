import { screen } from '@testing-library/react'
import App from './App'
import { jsonResponse, renderAuthenticated } from './test/render'

describe('removed customer admin routes', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(jsonResponse([]))))
  })
  it.each([
    '/layouts',
    '/layouts/layout-1/editor',
    '/catalog',
    '/containers',
    '/data-quality',
  ])('direct navigation to %s does not render the removed page', (route) => {
    renderAuthenticated(<App />, route)

    expect(screen.getByRole('heading', { name: 'Bu yönetim sayfası bulunamadı.' })).toBeVisible()
    expect(screen.queryByText('Layout sürümleri yükleniyor…')).not.toBeInTheDocument()
    expect(screen.queryByText('Editör')).not.toBeInTheDocument()
    const links = screen.getAllByRole('link').map((link) => link.getAttribute('href'))
    expect(links.some((href) => href === '/layouts' || href === '/catalog' || href === '/containers' || href === '/data-quality')).toBe(false)
  })

  it('keeps the allowed admin landing routes mounted', () => {
    renderAuthenticated(<App />, '/users')

    expect(screen.queryByRole('heading', { name: 'Bu yönetim sayfası bulunamadı.' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Kullanıcılar & Yetkiler' })).toBeVisible()
    expect(screen.getByRole('link', { name: 'RDS Entegrasyonu' })).toBeVisible()
    expect(screen.getByRole('link', { name: 'Audit Geçmişi' })).toBeVisible()
  })
})
