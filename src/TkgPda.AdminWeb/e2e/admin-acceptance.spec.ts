import { expect, loginAsAdmin, test, uniqueCode } from './support'

// Scenarios 02, 03, 04, 05, 06, 07, 13 against the real API + seeded PostgreSQL.

test('02 · dashboard renders real backend data (nine canonical machines)', async ({ page }) => {
  await loginAsAdmin(page)
  const machineCard = page.locator('a.stat-card', { hasText: 'Tanımlı makine' })
  await expect(machineCard.locator('strong')).toHaveText('9')
  await expect(page.locator('a.stat-card', { hasText: 'Aktif PDA' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Devreye Alma Hazırlığı' })).toHaveCount(0)

  // The nine machine masters are the real seeded records, not layout MACHINE regions.
  await page.getByRole('link', { name: 'Makine & Varyantlar' }).click()
  await expect(page.getByText('SH-01-A').first()).toBeVisible()
})

test.describe.serial('user lifecycle', () => {
  const username = uniqueCode('e2euser').toLowerCase()
  const password = `User!${uniqueCode('PASSWORD')}`

  test('03 · create a user (password never surfaces in the DOM)', async ({ page }) => {
    await loginAsAdmin(page)
    await page.getByRole('link', { name: 'Kullanıcılar & Yetkiler' }).click()
    await page.getByRole('button', { name: 'Kullanıcı oluştur' }).click()

    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Kullanıcı adı').fill(username)
    await dialog.getByLabel('Ad soyad').fill('E2E Test Kullanıcısı')
    await dialog.getByLabel('Sicil numarası').fill('E2E-100')
    await dialog.getByLabel('Geçici parola').fill(password)
    await dialog.getByRole('button', { name: 'Oluştur' }).click()

    await expect(page.getByText('Kullanıcı oluşturuldu')).toBeVisible()
    await page.getByPlaceholder('Ad, kullanıcı adı veya sicil ara…').fill(username)
    await expect(page.getByText(`@${username}`)).toBeVisible()

    // Secret safety: the provisioned password must not be rendered anywhere in the page.
    expect(await page.content()).not.toContain(password)
  })

  test('04 · change the user role', async ({ page }) => {
    await loginAsAdmin(page)
    await page.getByRole('link', { name: 'Kullanıcılar & Yetkiler' }).click()
    await page.getByPlaceholder('Ad, kullanıcı adı veya sicil ara…').fill(username)
    const row = page.locator('tr', { hasText: `@${username}` })
    await row.getByRole('button', { name: 'Düzenle' }).click()

    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Operatör Lideri', { exact: true }).check()
    await dialog.getByRole('button', { name: 'Kaydet' }).click()

    await expect(page.getByText('Kullanıcı güncellendi')).toBeVisible()
    await page.getByPlaceholder('Ad, kullanıcı adı veya sicil ara…').fill(username)
    await expect(page.locator('tr', { hasText: `@${username}` }).getByText('Operatör Lideri', { exact: true })).toBeVisible()
  })

  test('05 · modify a machine permission for the user', async ({ page }) => {
    await loginAsAdmin(page)
    await page.getByRole('link', { name: 'Kullanıcılar & Yetkiler' }).click()
    await page.getByPlaceholder('Ad, kullanıcı adı veya sicil ara…').fill(username)
    const row = page.locator('tr', { hasText: `@${username}` })
    await row.getByRole('button', { name: 'Yetkiler' }).click()

    // The checkbox is server-driven (click → API mutation → refetch), so click without
    // asserting an immediate local toggle, then confirm the toast and the persisted state.
    const permissionRow = page.locator('.permission-row', { hasText: 'SH-01-A' })
    await permissionRow.getByLabel('Görüntüle').click()
    await expect(page.getByText('Makine yetkisi güncellendi')).toBeVisible()

    // Re-open to confirm the grant persisted through the real API.
    await page.getByRole('button', { name: 'Tamam' }).click()
    await page.getByPlaceholder('Ad, kullanıcı adı veya sicil ara…').fill(username)
    await page.locator('tr', { hasText: `@${username}` }).getByRole('button', { name: 'Yetkiler' }).click()
    await expect(page.locator('.permission-row', { hasText: 'SH-01-A' }).getByLabel('Görüntüle')).toBeChecked()
  })
})

test.describe.serial('device lifecycle', () => {
  const code = uniqueCode('E2E-PDA')

  test('06 · register a PDA device', async ({ page }) => {
    await loginAsAdmin(page)
    await page.getByRole('link', { name: 'Cihazlar' }).click()
    await page.getByRole('button', { name: 'Cihaz ekle' }).click()

    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Cihaz kodu').fill(code)
    await dialog.getByLabel('Görünen ad').fill('E2E Saha PDA')
    await dialog.getByRole('button', { name: 'Kaydet' }).click()

    await expect(page.getByText('Cihaz sağlandı')).toBeVisible()
    await page.getByPlaceholder('Cihaz kodu, ad veya tip ara…').fill(code)
    const row = page.locator('tr', { hasText: code })
    await expect(row.getByText('Aktif', { exact: true })).toBeVisible()
  })

  test('07 · deactivate the PDA device', async ({ page }) => {
    await loginAsAdmin(page)
    await page.getByRole('link', { name: 'Cihazlar' }).click()
    await page.getByPlaceholder('Cihaz kodu, ad veya tip ara…').fill(code)
    const row = page.locator('tr', { hasText: code })
    await row.getByRole('button', { name: 'Düzenle' }).click()

    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Aktif cihaz').uncheck()
    await dialog.getByRole('button', { name: 'Kaydet' }).click()

    await expect(page.getByText('Cihaz güncellendi')).toBeVisible()
    await page.getByPlaceholder('Cihaz kodu, ad veya tip ara…').fill(code)
    await expect(page.locator('tr', { hasText: code }).getByText('Pasif', { exact: true })).toBeVisible()
  })
})

test('13 · retired configuration pages stay out of the operator-leader navigation', async ({ page }) => {
  await loginAsAdmin(page)
  await expect(page.getByRole('link', { name: 'Devreye Alma Hazırlığı' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Fiziksel Layoutlar' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Veri Kalitesi' })).toHaveCount(0)
})
