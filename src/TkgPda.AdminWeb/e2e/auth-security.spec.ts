import { dumpWebStorage, expect, indexedDbDatabases, loginAsAdmin, test } from './support'

// Scenarios 01, 14, 15 and the Phase 06.1 token-storage security proofs.

test('01 · admin login opens the live dashboard without a device code', async ({ page }) => {
  const deviceHeaders: string[] = []
  page.on('request', (request) => {
    const header = request.headers()['x-device-code']
    if (header) deviceHeaders.push(header)
  })
  await loginAsAdmin(page)
  await expect(page.getByText('TKG PDA / Yönetim Paneli')).toBeVisible()
  expect(deviceHeaders, 'Admin Web must never send X-Device-Code').toHaveLength(0)
})

test('security · access token lives only in memory — never in web storage, IndexedDB, URL or console', async ({ page }) => {
  const consoleLines: string[] = []
  page.on('console', (message) => consoleLines.push(message.text()))
  page.on('pageerror', (error) => consoleLines.push(error.message))

  let capturedToken = ''
  page.on('response', async (response) => {
    if (response.url().includes('/api/v1/admin/auth/login') && response.request().method() === 'POST' && response.ok()) {
      try {
        capturedToken = ((await response.json()) as { accessToken?: string }).accessToken ?? ''
      } catch {
        /* ignore non-JSON */
      }
    }
  })

  await loginAsAdmin(page)

  // Sanity: we really captured the issued JWT so the negative assertions below are meaningful.
  expect(capturedToken).not.toBe('')
  expect(capturedToken.split('.').length, 'issued value should be a JWT').toBe(3)

  const storage = await dumpWebStorage(page)
  const storageDump = JSON.stringify(storage)
  expect(storage.local, 'localStorage must be empty of admin state').toEqual({})
  expect(storage.session, 'sessionStorage must be empty of admin state').toEqual({})
  expect(storageDump).not.toContain(capturedToken)
  expect(storageDump).not.toContain('eyJ') // no JWT anywhere in web storage

  const databases = await indexedDbDatabases(page)
  expect(databases, 'the app must not persist auth into IndexedDB').toEqual([])

  expect(page.url()).not.toContain(capturedToken)
  expect(page.url()).not.toMatch(/eyJ|token=/)
  expect(await page.evaluate(() => document.cookie)).not.toContain(capturedToken)

  const leakedConsole = consoleLines.filter((line) => line.includes(capturedToken) || line.includes('eyJ'))
  expect(leakedConsole, 'the access token must never be written to the console').toEqual([])
})

test('15 · a full browser reload discards the in-memory session and forces re-login', async ({ page }) => {
  await loginAsAdmin(page)
  // Production redirects /admin to the canonical trailing-slash URL before
  // serving the SPA; navigate there directly so Vite exercises the same reload.
  await page.goto('/admin/')
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Genel Bakış' })).toHaveCount(0)
  const storage = await dumpWebStorage(page)
  expect(storage.local).toEqual({})
  expect(storage.session).toEqual({})
})

test('14 · logout clears authenticated state and protected routes cannot be reopened', async ({ page }) => {
  await loginAsAdmin(page)
  await page.getByRole('button', { name: 'Güvenli çıkış' }).click()
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()

  // Attempting a protected route after logout redirects back to the login screen.
  await page.goto('/admin/users')
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Kullanıcılar & yetkiler' })).toHaveCount(0)
})

test('guard · an unauthenticated protected route redirects to login', async ({ page }) => {
  await page.goto('/admin/audit')
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Teknik Denetim Geçmişi' })).toHaveCount(0)
})

test('401 · a server rejection clears the session and returns the operator to login', async ({ page }) => {
  await loginAsAdmin(page)
  // Simulate the server rejecting the bearer token (e.g. expiry/revocation) on the next admin call.
  await page.route('**/api/v1/admin/**', (route) =>
    route.fulfill({
      status: 401,
      contentType: 'application/problem+json',
      body: JSON.stringify({ status: 401, title: 'Unauthorized', detail: 'Token expired', code: 'AUTHENTICATION_REQUIRED' }),
    }))
  // In-app navigation (no reload) to a screen whose query is not yet cached, forcing a fresh
  // authenticated request that the server rejects with 401 — the client must then clear the session.
  await page.getByRole('link', { name: 'Denetim Geçmişi' }).click()
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
  const storage = await dumpWebStorage(page)
  expect(storage.local).toEqual({})
  expect(storage.session).toEqual({})
  await page.unroute('**/api/v1/admin/**')
})
