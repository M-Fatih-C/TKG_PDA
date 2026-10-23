import { expect, loginAsAdmin, test } from './support'

test('removed administration routes cannot be opened from an authenticated session', async ({ page }) => {
  await loginAsAdmin(page)

  for (const route of ['/commissioning', '/layouts', '/catalog', '/containers', '/data-quality']) {
    await page.evaluate((path) => {
      window.history.pushState({}, '', `/admin${path}`)
      window.dispatchEvent(new PopStateEvent('popstate'))
    }, route)
    await expect(page.getByRole('heading', { name: 'Bu yönetim sayfası bulunamadı.' })).toBeVisible()
  }
})
