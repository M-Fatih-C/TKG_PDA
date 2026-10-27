import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { render } from '@testing-library/react'
import { AuthProvider } from '../auth/AuthContext'
import { jsonResponse } from '../test/render'
import { LoginPage } from './LoginPage'

describe('Admin login', () => {
  it('shows invalid credentials in Turkish', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({
      status: 401,
      title: 'Unauthorized',
      detail: 'The username or password is invalid.',
      code: 'INVALID_CREDENTIALS',
    }, { status: 401 })))

    render(<MemoryRouter><AuthProvider><LoginPage /></AuthProvider></MemoryRouter>)
    await userEvent.type(screen.getByLabelText('Kullanıcı adı'), 'wrong-user')
    await userEvent.type(screen.getByLabelText('Parola'), 'wrong-password')
    await userEvent.click(screen.getByRole('button', { name: /Yönetim merkezine gir/ }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Kullanıcı adı veya parola geçersiz.')
  })
})
