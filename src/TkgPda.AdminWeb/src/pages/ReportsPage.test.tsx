import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ReportsPage } from './ReportsPage'
import { AuditPage } from './AuditPage'
import { jsonResponse, renderAuthenticated } from '../test/render'
import type { OperationsSummary, SessionReportPage, TaskReportDetail, TaskReportPage } from '../api/types'

const task = {
  requestId: '11111111-1111-1111-1111-111111111111',
  requestNo: 42,
  createdAt: '2026-10-07T05:31:04Z',
  requestedByName: 'Fatih Çetintaş',
  employeeNo: '1234',
  username: 'fatih',
  deviceCode: 'SH01A-TERM01',
  deviceName: 'Terminal',
  machineCode: 'SH-01-A',
  machineName: 'Makine A',
  variantCode: '1',
  operationLabel: 'Malzeme Getir',
  partCode: 'ŞİŞE-01',
  status: 'COMPLETED',
  statusLabel: 'Başarıyla tamamlandı',
  isSuccess: true,
  failureCode: null,
  failureMessage: null,
  failureSummary: null,
  cancellationLabel: null,
  cancelledByName: null,
  agvId: 'Sfl-Cbd15',
  endedReason: null,
  totalSeconds: 239,
  warehouseResponseSeconds: 131,
  robotExecutionSeconds: 96,
  preparationOperatorName: 'Mehmet Yılmaz',
  preparationEmployeeNo: '3333',
  preparationArea: 'Hazırlık Alanı 2',
  correlationId: '22222222-2222-2222-2222-222222222222',
}

const failed = { ...task, requestId: '33333333-3333-3333-3333-333333333333', requestNo: 43, status: 'FAILED', statusLabel: 'Hata ile sonlandı', isSuccess: false, failureCode: 'MOTOR_CHECK_TIMEOUT', failureSummary: 'Malzeme alma görevi başarısız oldu.' }
const cancelled = { ...task, requestId: '44444444-4444-4444-4444-444444444444', requestNo: 44, status: 'CANCELLED', statusLabel: 'İptal edildi', isSuccess: false, cancellationLabel: 'Operatör iptali' }

const page = (items = [task, failed, cancelled]): TaskReportPage => ({ items, total: items.length, page: 1, pageSize: 50 })
const detail: TaskReportDetail = { task, timeline: [{ at: task.createdAt, title: 'Talep oluşturuldu', actorLine: 'Fatih Çetintaş · Sicil 1234 · SH01A-TERM01' }] }
const sessions: SessionReportPage = {
  items: [{
    sessionId: '55555555-5555-5555-5555-555555555555',
    fullName: 'Ayşe Demir',
    employeeNo: '4455',
    username: 'ayse',
    roles: ['LINE_OPERATOR'],
    loginAt: '2026-10-07T05:00:00Z',
    endedAt: '2026-10-07T05:02:05Z',
    durationSeconds: 125,
    deviceCode: 'SH01A-TERM01',
    deviceName: 'Terminal',
    deviceType: 'PDA',
    machineState: 'UNKNOWN',
    machineCode: null,
    statusLabel: 'Süre doldu',
    endReasonLabel: 'Süre doldu',
  }],
  total: 60,
  page: 1,
  pageSize: 50,
  blockedAttempts: [{ at: '2026-10-07T05:01:00Z', fullName: 'Ayşe Demir', employeeNo: '4455', attemptedDeviceCode: 'B', existingDeviceCode: 'A', message: 'Bu sicil başka bir cihazda zaten aktifti.' }],
}
const summary: OperationsSummary = {
  total: 4, completed: 2, failed: 1, cancelled: 1, active: 0, successRate: 0.5,
  averageTotalSeconds: 239, averageWarehouseSeconds: 131, averageRobotSeconds: 96,
  byMachine: [{ key: 'SH-01-A', label: 'Makine A', count: 4 }],
  byOperation: [{ key: 'BRING_FULL', label: 'Malzeme Getir', count: 4 }],
  byRequester: [],
  topFailures: [{ key: 'MOTOR_CHECK_TIMEOUT', label: 'MOTOR_CHECK_TIMEOUT', count: 1 }],
}

const stub = (tasks: TaskReportPage = page()) => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    if (url.includes('/reports/tasks/export.csv') || url.includes('/reports/sessions/export.csv')) return new Response('csv', { status: 200, headers: { 'Content-Type': 'text/csv' } })
    if (url.includes('/reports/tasks/')) return jsonResponse(detail)
    if (url.includes('/reports/tasks')) return jsonResponse(tasks)
    if (url.includes('/reports/sessions')) return jsonResponse(sessions)
    if (url.includes('/reports/summary')) return jsonResponse(summary)
    if (url.includes('/audit-logs')) return jsonResponse([{ id: 1, actorUserId: null, actorUsername: 'fatih', action: 'REQUEST_CREATED', entityType: 'TRANSPORT_REQUEST', entityId: task.requestId, before: null, after: { ok: true }, ipAddress: null, userAgent: null, correlationId: task.correlationId, createdAt: task.createdAt }])
    if (url.includes('/machines')) return jsonResponse([{ id: 'm1', code: 'SH-01-A', displayName: 'Makine A', displayOrder: 1, isActive: true, concurrencyToken: 1 }])
    if (url.includes('/devices')) return jsonResponse([])
    return jsonResponse([])
  }))
}

describe('operations history', () => {
  it('shows an empty task period', async () => {
    stub(page([]))
    renderAuthenticated(<ReportsPage />, '/reports')
    expect(await screen.findByText('Bu dönemde görev yok')).toBeVisible()
  })

  it('shows loading and then task rows with status badges', async () => {
    let release: (value: Response) => void = () => undefined
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/reports/tasks') && !url.includes('/reports/tasks/')) {
        return new Promise<Response>((resolve) => { release = resolve })
      }
      return Promise.resolve(jsonResponse([]))
    }))
    renderAuthenticated(<ReportsPage />, '/reports')
    expect(screen.getByText('Görev geçmişi yükleniyor…')).toBeVisible()
    release(jsonResponse(page()))
    expect(await screen.findByText('42')).toBeVisible()
    expect(screen.getAllByText('Başarıyla tamamlandı').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Hata ile sonlandı').length).toBeGreaterThan(0)
    expect(screen.getAllByText('İptal edildi').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Fatih Çetintaş').length).toBeGreaterThan(0)
  })

  it('shows an error state', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ title: 'Hata', status: 500, detail: 'Rapor okunamadı', code: 'X' }), { status: 500, headers: { 'Content-Type': 'application/json' } })))
    renderAuthenticated(<ReportsPage />, '/reports')
    expect(await screen.findByRole('alert')).toBeVisible()
  })

  it('filters by date, machine and status and pages on the server', async () => {
    stub()
    const user = userEvent.setup()
    renderAuthenticated(<ReportsPage />, '/reports')
    expect(await screen.findByText('42')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Son 7 gün' }))
    await user.selectOptions(screen.getByLabelText('Makine'), 'm1')
    await user.selectOptions(screen.getByLabelText('Durum'), 'FAILED')
    await waitFor(() => {
      const calls = vi.mocked(fetch).mock.calls.map((call) => String(call[0]))
      expect(calls.some((url) => url.includes('machineId=m1') && url.includes('status=FAILED'))).toBe(true)
    })
  })

  it('opens the timeline and links to technical audit', async () => {
    stub()
    const user = userEvent.setup()
    renderAuthenticated(<ReportsPage />, '/reports')
    await user.click(await screen.findByText('42'))
    expect(await screen.findByText(/Talep oluşturuldu/)).toBeVisible()
    expect(screen.getAllByText(/Sicil 1234/).length).toBeGreaterThan(0)
    await user.click(screen.getByText('Teknik Ayrıntılar'))
    const link = screen.getByRole('link', { name: 'Teknik denetime git' })
    expect(link).toHaveAttribute('href', expect.stringContaining('correlationId=22222222-2222-2222-2222-222222222222'))
  })

  it('shows sessions, blocked login, summary and csv download', async () => {
    stub()
    const user = userEvent.setup()
    renderAuthenticated(<ReportsPage />, '/reports')
    await user.click(await screen.findByRole('tab', { name: 'Kullanıcı Oturumları' }))
    expect(await screen.findByText('Ayşe Demir')).toBeVisible()
    expect(screen.getByText('Geçmiş makine bilgisi kayıtlı değil')).toBeVisible()
    expect(screen.getAllByText('Süre doldu').length).toBeGreaterThan(0)
    expect(screen.getByText(/Bu sicil başka bir cihazda zaten aktifti/)).toBeVisible()
    expect(screen.getByRole('button', { name: 'Sonraki' })).toBeVisible()
    await user.click(screen.getByRole('tab', { name: 'Operasyon Özeti' }))
    expect(await screen.findByText('50%')).toBeVisible()
    expect(screen.getByText('Toplam görev')).toBeVisible()
    await user.click(screen.getByRole('tab', { name: 'Hata / İptal Analizi' }))
    expect(await screen.findByText(/MOTOR_CHECK_TIMEOUT/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'CSV indir' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some((call) => String(call[0]).includes('export.csv'))).toBe(true))
  })

  it('keeps the report table available at a desktop width', async () => {
    stub()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1440 })
    renderAuthenticated(<ReportsPage />, '/reports')
    expect(await screen.findByRole('columnheader', { name: 'Talep No' })).toBeVisible()
    expect(screen.getByRole('columnheader', { name: 'Bilgisayar' })).toBeVisible()
  })
})

describe('technical audit drill-down', () => {
  it('requests the correlation from the server', async () => {
    stub()
    renderAuthenticated(<AuditPage />, '/audit?correlationId=22222222-2222-2222-2222-222222222222&entityId=11111111-1111-1111-1111-111111111111')
    expect(await screen.findByText('Talep oluşturuldu')).toBeVisible()
    expect(screen.getByRole('heading', { name: 'Teknik Denetim Geçmişi' })).toBeVisible()
    await waitFor(() => {
      expect(vi.mocked(fetch).mock.calls.some((call) => String(call[0]).includes('correlationId=22222222-2222-2222-2222-222222222222'))).toBe(true)
    })
  })
})
