import { expect, type APIRequestContext, type Page } from '@playwright/test'
import { readRuntime, type RuntimeInfo } from './harness'

export { expect, test } from '@playwright/test'

// Read lazily: the runtime file is written by global setup, which runs after test collection.
let cached: RuntimeInfo | null = null
const runtime = (): RuntimeInfo => (cached ??= readRuntime())

let sequence = 0
/** Short, collision-free code for entities created during a run (uppercase, no spaces). */
export const uniqueCode = (prefix: string): string => {
  sequence += 1
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${sequence}`
}

export async function loginAsAdmin(page: Page): Promise<void> {
  const { adminUsername, adminPassword } = runtime()
  await page.goto('/admin/login')
  await page.getByLabel('Kullanıcı adı').fill(adminUsername)
  await page.getByLabel('Parola').fill(adminPassword)
  await page.getByRole('button', { name: 'Yönetim merkezine gir' }).click()
  await expect(page.getByRole('heading', { name: 'Genel bakış' })).toBeVisible()
}

/** Obtain a second, memory-only ADMIN_WEB bearer for genuine API concurrency tests. */
export async function loginAdminApi(request: APIRequestContext): Promise<string> {
  const { adminUsername, adminPassword } = runtime()
  const response = await request.post('/api/v1/admin/auth/login', {
    data: { username: adminUsername, password: adminPassword },
  })
  expect(response.ok(), `second ADMIN_WEB login failed with ${response.status()}`).toBe(true)
  const body = (await response.json()) as { accessToken?: string }
  expect(body.accessToken).toBeTruthy()
  return body.accessToken!
}

/** Read every value currently held in localStorage + sessionStorage for the active origin. */
export async function dumpWebStorage(page: Page): Promise<{ local: Record<string, string>; session: Record<string, string> }> {
  return page.evaluate(() => {
    const read = (store: Storage) => {
      const out: Record<string, string> = {}
      for (let i = 0; i < store.length; i += 1) {
        const key = store.key(i)
        if (key) out[key] = store.getItem(key) ?? ''
      }
      return out
    }
    return { local: read(window.localStorage), session: read(window.sessionStorage) }
  })
}

/** Enumerate IndexedDB database names (empty when the app never opens IndexedDB). */
export async function indexedDbDatabases(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    if (!('databases' in indexedDB)) return []
    const dbs = await indexedDB.databases()
    return dbs.map((db) => db.name ?? '').filter(Boolean)
  })
}
