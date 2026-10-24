import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { UsersPage } from './UsersPage'
import { machines, roles, users } from '../test/fixtures'
import { jsonResponse, renderAuthenticated } from '../test/render'

const mockUsersApi = () => vi.stubGlobal('fetch', vi.fn().mockImplementation((input: RequestInfo | URL) => {
  const url = String(input)
  if (url.endsWith('/users')) return Promise.resolve(jsonResponse(users))
  if (url.endsWith('/roles')) return Promise.resolve(jsonResponse(roles))
  if (url.endsWith('/machines')) return Promise.resolve(jsonResponse(machines))
  return Promise.resolve(jsonResponse([]))
}))

describe('User and permission management', () => {
  it('renders exact seeded roles and account state', async () => {
    mockUsersApi(); renderAuthenticated(<UsersPage />)
    expect((await screen.findAllByText('Hat Operatörü')).length).toBeGreaterThanOrEqual(2)
    expect(screen.queryByText('LINE_OPERATOR')).not.toBeInTheDocument()
    expect(screen.getAllByText('Aktif')).toHaveLength(2)
  })

  it('shows ADMIN global bypass explicitly in the permission matrix', async () => {
    mockUsersApi(); renderAuthenticated(<UsersPage />)
    await userEvent.click((await screen.findAllByRole('button', { name: /Yetkiler/ }))[0])
    expect(screen.getByText(/Yönetici rolü tüm makinelere erişebilir/)).toBeVisible()
  })

  it('shows persisted line-operator permissions', async () => {
    mockUsersApi(); renderAuthenticated(<UsersPage />)
    await userEvent.click((await screen.findAllByRole('button', { name: /Yetkiler/ }))[1])
    const checks = screen.getAllByRole('checkbox')
    expect(checks.filter((element) => (element as HTMLInputElement).checked)).toHaveLength(2)
  })
})
