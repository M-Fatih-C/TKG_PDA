import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RdsIntegrationPage } from './RdsIntegrationPage'
import { jsonResponse, renderAuthenticated } from '../test/render'

const profile = { id: 'np-1', machineId: 'm-1', machineCode: 'SH-01-A', code: 'NAV-A', name: 'Navigation A', version: 1, status: 'PUBLISHED', robotMapName: '26.7.2026_map_optimak_1' }
const workSite = { id: 'rw-1', navigationProfileId: 'np-1', siteId: 'SITE-01', area: 'A', groupName: 'G', type: 1, filled: 0, locked: 0, disabled: 0, syncFailed: 0, content: '', agvId: null, lastSyncAt: '2026-08-24T08:00:00Z', executionReady: true }
const taskTemplate = { id: 'tt-1', variantId: 'v-1', variantCode: 'V1', productGroupId: null, productGroupCode: null, operation: 'BRING_EMPTY', rdsTaskLabel: 'TKG_SIM_TASK', priority: 10, config: { rdsInputBindings: [{ param: 'fromSite', source: 'SOURCE_RDS_SITE_ID', required: true }] }, isActive: true }
const robotAlarm = { robotId: 'sim_01', robotModel: 'AMB-150', severity: 'fatal', code: 'F1', description: 'Fatal one', occurrenceCount: 3, occurredAt: '2026-08-30T10:00:00Z', dispatchable: false, undispatchableReason: 'low_battery' }

const mockRdsApi = (options?: { alarms?: unknown; alarmsResponse?: Response; robots?: unknown }) => {
  const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    if (url.endsWith('/rds/status')) return Promise.resolve(jsonResponse({ enabled: true, reachable: true, stale: false, lastSuccessfulRead: '2026-08-24T08:00:00Z', baseAddressDisplaySafe: 'http://rds.test:8080', businessStatus: 200, businessMessage: 'Success', errorCode: null, errorMessage: null }))
    if (url.endsWith('/rds/navigation-profiles')) return Promise.resolve(jsonResponse([profile]))
    if (url.includes('/rds/worksites?')) return Promise.resolve(jsonResponse([workSite]))
    if (url.endsWith('/rds/slot-mappings')) return Promise.resolve(jsonResponse([]))
    if (url.endsWith('/rds/task-templates')) return Promise.resolve(jsonResponse([taskTemplate]))
    if (url.endsWith('/rds/robots')) return Promise.resolve(jsonResponse(options?.robots ?? []))
    if (url.endsWith('/rds/alarms')) return Promise.resolve(options?.alarmsResponse ?? jsonResponse(options?.alarms ?? []))
    if (url.endsWith('/rds/task-templates/validate') && init?.method === 'POST') return Promise.resolve(jsonResponse({ validated: true, taskLabelExists: true, taskRecordExists: false, code: 200, message: 'VALIDATED', rawResponse: {} }))
    if (url.endsWith('/rds/worksites/sync') && init?.method === 'POST') return Promise.resolve(jsonResponse({ navigationProfileId: 'np-1', received: 1, created: 1, updated: 0, syncedAt: '2026-08-24T08:00:00Z', businessCode: 200, businessMessage: 'Success' }))
    return Promise.resolve(jsonResponse([]))
  })
  vi.stubGlobal('fetch', fetchMock)
  renderAuthenticated(<RdsIntegrationPage />)
  return fetchMock
}

describe('RDS integration page', () => {
  it('labels RDS data as external read evidence and does not mutate during load', async () => {
    const fetchMock = mockRdsApi()
    expect(await screen.findAllByText('RDS Entegrasyonu')).not.toHaveLength(0)
    expect(screen.getByText('Robot yürütme, canlı durum, alarm ve WorkSite eşleşmeleri.')).toBeVisible()
    expect(screen.getByText('RDS Bağlantısı')).toBeVisible()
    expect(screen.getByText('Eşleme Durumu')).toBeVisible()
    expect(screen.getByText('Aktif Robot Alarmları')).toBeVisible()
    expect(screen.getByText('WorkSite Senkronizasyonu')).toBeVisible()
    expect(screen.queryByText('Salt okunur dış entegrasyon')).not.toBeInTheDocument()
    expect(screen.getByText('RDS değerleri dış sistem kanıtıdır.')).toBeVisible()
    expect(await screen.findByText('SITE-01')).toBeVisible()
    expect(screen.getByText('http://rds.test:8080')).toBeVisible()
    expect(fetchMock.mock.calls.every(([, init]) => !init?.method || init.method === 'GET')).toBe(true)
  })

  it('requires an explicit scene confirmation before read-only synchronization', async () => {
    const fetchMock = mockRdsApi()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    await userEvent.click(await screen.findByRole('button', { name: /Senkronize et/ }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/rds/worksites/sync') && init?.method === 'POST')).toBe(true))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('şu anda RDS'))
  })

  it('shows explicit task bindings and validates through TKG without dispatching', async () => {
    const fetchMock = mockRdsApi()
    expect(await screen.findByText('TKG_SIM_TASK')).toBeVisible()
    expect(screen.getByText('fromSite')).toBeVisible()
    await userEvent.click(screen.getByRole('button', { name: /Doğrula/ }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/rds/task-templates/validate') && init?.method === 'POST')).toBe(true))
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('create-transport'))).toBe(false)
  })

  it('shows the current robot alarm table with severity, code and detail', async () => {
    mockRdsApi({ alarms: [robotAlarm] })
    expect(await screen.findByText('sim_01')).toBeVisible()
    expect(screen.getByText('AMB-150')).toBeVisible()
    expect(screen.getByText('FATAL')).toBeVisible()
    expect(screen.getByText('F1')).toBeVisible()
    expect(screen.getByText('Fatal one')).toBeVisible()
    expect(screen.getByText('3')).toBeVisible()
    expect(screen.getByText('2026-08-30T10:00:00Z')).toBeVisible()
    expect(screen.getByText(/NO.*low_battery/)).toBeVisible()
  })

  it('shows an empty state when there are no active robot alarms', async () => {
    mockRdsApi({ alarms: [] })
    expect(await screen.findByText('Aktif robot alarmı yok')).toBeVisible()
  })

  it('shows the robot fleet telemetry table with battery, station, map, order and status across normalized dimensions', async () => {
    const robotDetail = {
      robotId: 'robot_99',
      robotModel: 'AMB-150',
      uuid: 'robot_99',
      vehicleId: 'robot_99',
      connectivity: 'ONLINE',
      dispatch: 'DISPATCHABLE',
      undispatchableReason: null,
      motion: 'IDLE',
      execution: 'RUNNING',
      control: 'UNLOCKED',
      isLocked: false,
      lockNickName: null,
      batteryLevel: 0.85,
      isCharging: false,
      currentStation: 'LM1',
      lastStation: 'SM11',
      currentMap: '26.7.2026_map_optimak',
      activeOrderId: 'ORD-100',
      activeOrderState: 'RUNNING',
      rdsReportedPayloadLoaded: true,
      online: true,
      dispatchable: true,
      alarms: { fatal: 0, error: 0, warning: 0, notice: 0, total: 0, critical: 0 },
    }
    mockRdsApi({ robots: [robotDetail] })
    expect(await screen.findByText('Robot Filosu ve Canlı Telemetri')).toBeVisible()
    expect(await screen.findByText('robot_99')).toBeVisible()
    expect(screen.getByText('85%')).toBeVisible()
    expect(screen.getByText('LM1')).toBeVisible()
    expect(screen.getByText('26.7.2026_map_optimak')).toBeVisible()
    expect(screen.getByText('Emir: ORD-100')).toBeVisible()
    expect(screen.getByText('ONLINE')).toBeVisible()
    expect(screen.getByText('DISPATCHABLE')).toBeVisible()
    expect(screen.getByText('UNLOCKED')).toBeVisible()
    expect(screen.getByText('Yük Telemetrisi: YÜKLÜ (RDS)')).toBeVisible()
    expect(screen.getByText('1 / 1 Çevrimiçi')).toBeVisible()
    expect(screen.getByText('1 Sevk Edilebilir')).toBeVisible()
    expect(screen.getByText('0 Kilitli')).toBeVisible()
  })

  it('displays RDSCORE ROUTE-MANAGED MAP as neutral diagnostic during active order with multi-map transition', async () => {
    const runningMultiMapRobot = {
      robotId: 'robot_100',
      robotModel: 'AMB-150',
      uuid: 'robot_100',
      vehicleId: 'robot_100',
      connectivity: 'ONLINE',
      dispatch: 'DISPATCHABLE',
      undispatchableReason: null,
      motion: 'MOVING',
      execution: 'RUNNING',
      control: 'UNLOCKED',
      isLocked: false,
      lockNickName: null,
      batteryLevel: 0.90,
      isCharging: false,
      currentStation: 'SM11',
      lastStation: 'LM1',
      currentMap: '26.7.2026_map_optimak',
      activeOrderId: 'ORD-999',
      activeOrderState: 'RUNNING',
      rdsReportedPayloadLoaded: false,
      online: true,
      dispatchable: true,
      alarms: { fatal: 0, error: 0, warning: 0, notice: 0, total: 0, critical: 0 },
    }
    mockRdsApi({ robots: [runningMultiMapRobot] })
    const profileSelect = await screen.findByRole('combobox', { name: /Navigation profile/i })
    await userEvent.selectOptions(profileSelect, 'np-1')

    expect(await screen.findByText('RDSCORE ROTA YÖNETİMLİ HARİTA')).toBeVisible()
    expect(screen.queryByText('FARKLI HARİTA')).not.toBeInTheDocument()
  })


  it('represents ONLINE + UNDISPATCHABLE and external lock without TKG ownership inference', async () => {
    const blockedRobot = {
      robotId: 'robot_101',
      robotModel: 'AMB-150',
      uuid: 'robot_101',
      vehicleId: 'robot_101',
      connectivity: 'ONLINE',
      dispatch: 'UNDISPATCHABLE',
      undispatchableReason: 'low_battery',
      motion: 'BLOCKED',
      execution: 'IDLE',
      control: 'EXTERNAL_OR_OTHER',
      isLocked: true,
      lockNickName: 'ROBOSHOP_LOCK_1',
      batteryLevel: 0.15,
      isCharging: false,
      currentStation: 'CHARGE_01',
      lastStation: 'LM1',
      currentMap: '26.7.2026_map_optimak',
      activeOrderId: null,
      activeOrderState: null,
      rdsReportedPayloadLoaded: null,
      online: true,
      dispatchable: false,
      alarms: { fatal: 0, error: 1, warning: 0, notice: 0, total: 1, critical: 1 },
    }
    mockRdsApi({ robots: [blockedRobot] })
    expect(await screen.findByText('robot_101')).toBeVisible()
    expect(screen.getByText('ONLINE')).toBeVisible()
    expect(screen.getByText('UNDISPATCHABLE')).toBeVisible()
    expect(screen.getByText('low_battery')).toBeVisible()
    expect(screen.getByText('BLOCKED')).toBeVisible()
    expect(screen.getByText('EXTERNAL_OR_OTHER')).toBeVisible()
    expect(screen.queryByText('TKG_CORRELATED')).not.toBeInTheDocument()
  })

  it('does not treat a retained FINISHED order id as an active order', async () => {
    // SEER keeps reporting the previous order id after it FINISHES. Deriving
    // activity from activeOrderId marked an idle robot as route-managed and
    // suppressed the pre-dispatch diagnostic.
    const finishedRobot = {
      robotId: 'robot_102',
      robotModel: 'AMB-150',
      uuid: 'robot_102',
      vehicleId: 'robot_102',
      connectivity: 'ONLINE',
      dispatch: 'DISPATCHABLE',
      undispatchableReason: null,
      motion: 'IDLE',
      execution: 'FINISHED',
      control: 'UNLOCKED',
      isLocked: false,
      lockNickName: null,
      batteryLevel: 0.9,
      isCharging: false,
      currentStation: 'SM11',
      lastStation: 'LM1',
      // Deliberately different from the reference profile's map so that the
      // running-vs-idle branch is actually exercised.
      currentMap: '26.7.2026_map_optimak',
      activeOrderId: 'ORD-FINISHED-1',
      activeOrderState: 'FINISHED',
      rdsReportedPayloadLoaded: false,
      online: true,
      dispatchable: true,
      alarms: { fatal: 0, error: 0, warning: 0, notice: 0, total: 0, critical: 0 },
    }
    mockRdsApi({ robots: [finishedRobot] })
    const profileSelect = await screen.findByRole('combobox', { name: /Navigation profile/i })
    await userEvent.selectOptions(profileSelect, 'np-1')

    expect(await screen.findByText('robot_102')).toBeVisible()
    expect(screen.queryByText('RDSCORE ROTA YÖNETİMLİ HARİTA')).not.toBeInTheDocument()
    expect(screen.getByText('REFERANS PROFİLDEN FARKLI')).toBeVisible()
  })

  it('labels the manually selected navigation profile as a reference, not the robot profile', async () => {
    const idleRobot = {
      robotId: 'robot_103',
      robotModel: 'AMB-150',
      uuid: 'robot_103',
      vehicleId: 'robot_103',
      connectivity: 'ONLINE',
      dispatch: 'DISPATCHABLE',
      undispatchableReason: null,
      motion: 'IDLE',
      execution: 'IDLE',
      control: 'UNLOCKED',
      isLocked: false,
      lockNickName: null,
      batteryLevel: 0.9,
      isCharging: false,
      currentStation: 'SM11',
      lastStation: 'LM1',
      currentMap: '26.7.2026_map_optimak',
      activeOrderId: null,
      activeOrderState: null,
      rdsReportedPayloadLoaded: null,
      online: true,
      dispatchable: true,
      alarms: { fatal: 0, error: 0, warning: 0, notice: 0, total: 0, critical: 0 },
    }
    mockRdsApi({ robots: [idleRobot] })
    const profileSelect = await screen.findByRole('combobox', { name: /Navigation profile/i })
    await userEvent.selectOptions(profileSelect, 'np-1')

    expect(await screen.findByText('robot_103')).toBeVisible()
    // No profile is claimed to be authoritative for this robot.
    expect(screen.getByText(/Referans Profil Haritası/)).toBeVisible()
    expect(screen.queryByText(/Ön Sevk Haritası/)).not.toBeInTheDocument()
    expect(screen.queryByText('ÖN SEVK HAZIR')).not.toBeInTheDocument()
  })

  it('does not present benign notices as robot faults', async () => {
    const noticeRobot = {
      robotId: 'robot_104',
      robotModel: 'AMB-150',
      uuid: 'robot_104',
      vehicleId: 'robot_104',
      connectivity: 'ONLINE',
      dispatch: 'DISPATCHABLE',
      undispatchableReason: null,
      motion: 'IDLE',
      execution: 'IDLE',
      control: 'UNLOCKED',
      isLocked: false,
      lockNickName: null,
      batteryLevel: 0.9,
      isCharging: false,
      currentStation: 'SM11',
      lastStation: 'LM1',
      currentMap: '26.7.2026_map_optimak',
      activeOrderId: null,
      activeOrderState: null,
      rdsReportedPayloadLoaded: null,
      online: true,
      dispatchable: true,
      alarms: { fatal: 0, error: 0, warning: 0, notice: 2, total: 2, critical: 0 },
    }
    mockRdsApi({ robots: [noticeRobot] })

    expect(await screen.findByText('robot_104')).toBeVisible()
    expect(screen.getByText('2 BİLGİ')).toBeVisible()
    expect(screen.queryByText('2 ARIZA')).not.toBeInTheDocument()
    // Fleet fault KPI counts only fatal + error.
    expect(screen.getByText('Aktif Arıza (Fatal + Error)')).toBeVisible()
    expect(screen.getByText('0 uyarı · 2 bilgi notu (arıza sayılmaz)')).toBeVisible()
  })

  it('renders UNKNOWN dimensions when telemetry is absent instead of assuming online and unlocked', async () => {
    const unknownRobot = {
      robotId: 'robot_105',
      robotModel: null,
      uuid: 'robot_105',
      vehicleId: 'robot_105',
      connectivity: 'UNKNOWN',
      dispatch: 'UNKNOWN',
      undispatchableReason: null,
      motion: 'UNKNOWN',
      execution: 'IDLE',
      control: 'UNKNOWN',
      isLocked: null,
      lockNickName: null,
      batteryLevel: null,
      isCharging: null,
      currentStation: null,
      lastStation: null,
      currentMap: null,
      activeOrderId: null,
      activeOrderState: null,
      rdsReportedPayloadLoaded: null,
      online: null,
      dispatchable: null,
      alarms: { fatal: 0, error: 0, warning: 0, notice: 0, total: 0, critical: 0 },
    }
    mockRdsApi({ robots: [unknownRobot] })

    expect(await screen.findByText('robot_105')).toBeVisible()
    expect(screen.getAllByText('UNKNOWN').length).toBeGreaterThanOrEqual(3)
    expect(screen.queryByText('ONLINE')).not.toBeInTheDocument()
    expect(screen.queryByText('UNLOCKED')).not.toBeInTheDocument()
    // An unknown-connectivity robot is not counted as online.
    expect(screen.getByText('0 / 1 Çevrimiçi')).toBeVisible()
    expect(screen.getByText('0 Kilitli')).toBeVisible()
  })

  it('polls the fleet snapshot without duplicating the upstream alarm read', async () => {
    // /admin/rds/robots and /admin/rds/alarms both resolve to the same upstream
    // GET /api/agv-report/core, so only the fleet snapshot is polled.
    const fetchMock = mockRdsApi({ robots: [] })
    await screen.findByText('Robot Filosu ve Canlı Telemetri')

    const alarmCalls = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/rds/alarms')).length
    const robotCalls = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/rds/robots')).length
    expect(alarmCalls).toBe(1)
    expect(robotCalls).toBe(1)

    const refreshSelect = await screen.findByRole('combobox', { name: /Otomatik yenileme/i })
    await userEvent.selectOptions(refreshSelect, '5000')
    // Selecting an interval must not immediately re-fetch the alarm detail table.
    expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/rds/alarms')).length).toBe(1)
  })

  it('shows a distinct failure state with retry when the alarm read fails, not an empty table', async () => {
    const fetchMock = mockRdsApi({
      alarmsResponse: jsonResponse({ status: 503, title: 'Service Unavailable', detail: 'RDS okuma servisi geçici olarak kullanılamıyor.', code: 'RDS_UNAVAILABLE', correlationId: 'corr-1' }, { status: 503 }),
    })
    expect(await screen.findByText('RDS okuma servisi geçici olarak kullanılamıyor.')).toBeVisible()
    expect(screen.queryByText('Aktif robot alarmı yok')).not.toBeInTheDocument()

    await userEvent.click(screen.getAllByRole('button', { name: /Yenile/ })[0])
    await waitFor(() => expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/rds/alarms')).length).toBeGreaterThan(1))
  })
})
