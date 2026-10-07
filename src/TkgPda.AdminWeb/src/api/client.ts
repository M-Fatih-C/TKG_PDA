import type {
  AdminSession,
  AdminUser,
  AuditRecord,
  ContainerType,
  DataIssue,
  Device,
  DeviceDraft,
  JsonObject,
  Layout,
  Machine,
  MachinePermission,
  Part,
  ProblemDetails,
  ProductGroup,
  Role,
  RoleCode,
  RdsConnectionStatus,
  RdsNavigationProfile,
  RdsRobotAlarm,
  RdsRobotDetail,
  RdsSlotMapping,
  RdsSyncResult,
  RdsWorkSite,
  RdsTaskTemplate,
  RdsTaskTemplateValidation,
  ProductionEndPriority,
  TransportEvidence,
  TaskReportPage,
  VariantOperationReadiness,
  TaskReportDetail,
  SessionReportPage,
  OperationsSummary,
  ReportWindow,
  Slot,
  SlotDraft,
  SlotPartRecord,
  UserDraft,
  Variant,
} from './types'

export class ApiProblem extends Error {
  readonly status: number
  readonly code: string
  readonly correlationId?: string

  constructor(problem: ProblemDetails) {
    super(problem.detail || problem.title)
    this.name = 'ApiProblem'
    this.status = problem.status
    this.code = problem.code || 'HTTP_ERROR'
    this.correlationId = problem.correlationId
  }

  get isConcurrencyConflict() {
    return this.status === 409 && this.code === 'CONCURRENCY_CONFLICT'
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  token?: string | null
  body?: unknown
  ifMatch?: number
  layoutVersion?: number
  signal?: AbortSignal
}

export interface ApiResult<T> {
  data: T
  etag: number | null
}

const request = async <T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> => {
  const headers = new Headers({ Accept: 'application/json' })
  if (options.body !== undefined) headers.set('Content-Type', 'application/json')
  if (options.token) headers.set('Authorization', `Bearer ${options.token}`)
  if (options.ifMatch !== undefined) headers.set('If-Match', `"${options.ifMatch}"`)
  if (options.layoutVersion !== undefined) headers.set('X-Layout-Version', `"${options.layoutVersion}"`)

  const response = await fetch(path, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  })

  if (!response.ok) {
    let problem: ProblemDetails
    try {
      problem = (await response.json()) as ProblemDetails
    } catch {
      problem = {
        status: response.status,
        title: response.statusText || 'İstek başarısız',
        detail: 'Sunucu beklenen ProblemDetails yanıtını üretmedi.',
        code: 'UNEXPECTED_HTTP_RESPONSE',
      }
    }
    if (response.status === 401 && options.token) window.dispatchEvent(new Event('tkg:unauthorized'))
    throw new ApiProblem(problem)
  }

  const etagValue = response.headers.get('etag')?.replaceAll('"', '')
  const etag = etagValue && Number.isFinite(Number(etagValue)) ? Number(etagValue) : null
  if (response.status === 204) return { data: undefined as T, etag }
  return { data: (await response.json()) as T, etag }
}

export const loginAdmin = async (username: string, password: string) =>
  (await request<AdminSession>('/api/v1/admin/auth/login', { method: 'POST', body: { username, password } })).data

export const createAdminApi = (token: string) => ({
  roles: async (signal?: AbortSignal) => (await request<Role[]>('/api/v1/admin/roles', { token, signal })).data,
  users: async (signal?: AbortSignal) => (await request<AdminUser[]>('/api/v1/admin/users', { token, signal })).data,
  createUser: async (draft: UserDraft) => (await request<AdminUser>('/api/v1/admin/users', { method: 'POST', token, body: draft })).data,
  updateUser: async (user: AdminUser, draft: Pick<AdminUser, 'fullName' | 'employeeNo' | 'isActive'>) =>
    (await request<AdminUser>(`/api/v1/admin/users/${user.id}`, { method: 'PUT', token, ifMatch: user.concurrencyToken, body: draft })).data,
  resetPassword: async (user: AdminUser, newPassword: string) =>
    (await request<AdminUser>(`/api/v1/admin/users/${user.id}/password-reset`, { method: 'POST', token, ifMatch: user.concurrencyToken, body: { newPassword } })).data,
  setRoles: async (user: AdminUser, roles: RoleCode[]) =>
    (await request<AdminUser>(`/api/v1/admin/users/${user.id}/roles`, { method: 'PUT', token, ifMatch: user.concurrencyToken, body: { roles } })).data,
  setPermission: async (user: AdminUser, permission: MachinePermission) =>
    (await request<MachinePermission>(`/api/v1/admin/users/${user.id}/machine-permissions/${permission.machineId}`, {
      method: 'PUT', token, ifMatch: user.concurrencyToken, body: permission,
    })).data,
  removePermission: async (user: AdminUser, machineId: string) =>
    (await request<void>(`/api/v1/admin/users/${user.id}/machine-permissions/${machineId}`, { method: 'DELETE', token, ifMatch: user.concurrencyToken })).data,

  devices: async (signal?: AbortSignal) => (await request<Device[]>('/api/v1/admin/devices', { token, signal })).data,
  createDevice: async (draft: DeviceDraft) => (await request<Device>('/api/v1/admin/devices', { method: 'POST', token, body: draft })).data,
  updateDevice: async (device: Device, draft: Omit<DeviceDraft, 'code'>) =>
    (await request<Device>(`/api/v1/admin/devices/${device.id}`, { method: 'PUT', token, ifMatch: device.concurrencyToken, body: draft })).data,

  machines: async (signal?: AbortSignal) => (await request<Machine[]>('/api/v1/admin/machines', { token, signal })).data,
  updateMachine: async (machine: Machine, draft: Pick<Machine, 'displayName' | 'displayOrder' | 'isActive'>) =>
    (await request<Machine>(`/api/v1/admin/machines/${machine.id}`, { method: 'PUT', token, ifMatch: machine.concurrencyToken, body: draft })).data,
  variants: async (machineId?: string, signal?: AbortSignal) =>
    (await request<Variant[]>(`/api/v1/admin/variants${machineId ? `?machineId=${machineId}` : ''}`, { token, signal })).data,
  createVariant: async (draft: Pick<Variant, 'machineId' | 'code' | 'name' | 'displayOrder'>) =>
    (await request<Variant>('/api/v1/admin/variants', { method: 'POST', token, body: draft })).data,
  updateVariant: async (variant: Variant, draft: Pick<Variant, 'name' | 'displayOrder' | 'isActive'>) =>
    (await request<Variant>(`/api/v1/admin/variants/${variant.id}`, { method: 'PUT', token, ifMatch: variant.concurrencyToken, body: draft })).data,
  setDefaultLayout: async (variant: Variant, layoutId: string) =>
    (await request<Variant>(`/api/v1/admin/variants/${variant.id}/default-layout`, { method: 'PUT', token, ifMatch: variant.concurrencyToken, body: { layoutId } })).data,

  layouts: async (machineId?: string, signal?: AbortSignal) =>
    (await request<Layout[]>(`/api/v1/admin/layouts${machineId ? `?machineId=${machineId}` : ''}`, { token, signal })).data,
  createLayout: async (draft: Pick<Layout, 'machineId' | 'code' | 'name'>) =>
    (await request<Layout>('/api/v1/admin/layouts', { method: 'POST', token, body: draft })).data,
  updateLayout: async (layout: Layout, name: string) =>
    (await request<Layout>(`/api/v1/admin/layouts/${layout.id}`, { method: 'PUT', token, ifMatch: layout.concurrencyToken, body: { name } })).data,
  cloneLayout: async (layout: Layout) =>
    (await request<Layout>(`/api/v1/admin/layouts/${layout.id}/versions`, { method: 'POST', token, ifMatch: layout.concurrencyToken })).data,
  publishLayout: async (layout: Layout) =>
    (await request<Layout>(`/api/v1/admin/layouts/${layout.id}/publish`, { method: 'POST', token, ifMatch: layout.concurrencyToken })).data,
  archiveLayout: async (layout: Layout) =>
    (await request<Layout>(`/api/v1/admin/layouts/${layout.id}/archive`, { method: 'POST', token, ifMatch: layout.concurrencyToken })).data,
  slots: async (layoutId: string, signal?: AbortSignal) =>
    (await request<Slot[]>(`/api/v1/admin/layouts/${layoutId}/slots`, { token, signal })).data,
  createSlot: async (layout: Layout, draft: Omit<SlotDraft, 'isEnabled'>) =>
    (await request<Slot>(`/api/v1/admin/layouts/${layout.id}/slots`, { method: 'POST', token, ifMatch: layout.concurrencyToken, body: draft })).data,
  updateSlot: async (layout: Layout, slot: Slot, draft: Omit<SlotDraft, 'code'>) =>
    (await request<Slot>(`/api/v1/admin/layouts/${layout.id}/slots/${slot.id}`, {
      method: 'PUT', token, ifMatch: slot.concurrencyToken, layoutVersion: layout.concurrencyToken, body: draft,
    })).data,
  slotParts: async (slotId: string, signal?: AbortSignal) =>
    (await request<SlotPartRecord[]>(`/api/v1/admin/layout-slots/${slotId}/parts`, { token, signal })).data,
  assignSlotPart: async (slotId: string, partId: string) =>
    (await request<void>(`/api/v1/admin/layout-slots/${slotId}/parts/${partId}`, { method: 'PUT', token })).data,
  removeSlotPart: async (slotId: string, partId: string) =>
    (await request<void>(`/api/v1/admin/layout-slots/${slotId}/parts/${partId}`, { method: 'DELETE', token })).data,

  parts: async (signal?: AbortSignal) => (await request<Part[]>('/api/v1/admin/parts', { token, signal })).data,
  createPart: async (draft: Pick<Part, 'code' | 'description'>) =>
    (await request<Part>('/api/v1/admin/parts', { method: 'POST', token, body: draft })).data,
  updatePart: async (part: Part, draft: Pick<Part, 'description' | 'isActive'>) =>
    (await request<Part>(`/api/v1/admin/parts/${part.id}`, { method: 'PUT', token, ifMatch: part.concurrencyToken, body: draft })).data,
  productGroups: async (variantId?: string, signal?: AbortSignal) =>
    (await request<ProductGroup[]>(`/api/v1/admin/product-groups${variantId ? `?variantId=${variantId}` : ''}`, { token, signal })).data,
  createProductGroup: async (draft: { variantId: string; mainPartId: string; name: string | null; displayOrder: number }) =>
    (await request<ProductGroup>('/api/v1/admin/product-groups', { method: 'POST', token, body: draft })).data,
  updateProductGroup: async (group: ProductGroup, draft: Pick<ProductGroup, 'name' | 'displayOrder' | 'isActive'>) =>
    (await request<ProductGroup>(`/api/v1/admin/product-groups/${group.id}`, { method: 'PUT', token, ifMatch: group.concurrencyToken, body: draft })).data,
  setProductGroupPart: async (group: ProductGroup, partId: string, draft: JsonObject) =>
    (await request<ProductGroup>(`/api/v1/admin/product-groups/${group.id}/parts/${partId}`, { method: 'PUT', token, ifMatch: group.concurrencyToken, body: draft })).data,
  removeProductGroupPart: async (group: ProductGroup, partId: string) =>
    (await request<void>(`/api/v1/admin/product-groups/${group.id}/parts/${partId}`, { method: 'DELETE', token, ifMatch: group.concurrencyToken })).data,

  containers: async (signal?: AbortSignal) => (await request<ContainerType[]>('/api/v1/admin/containers', { token, signal })).data,
  createContainer: async (draft: Omit<ContainerType, 'id' | 'isActive' | 'concurrencyToken'>) =>
    (await request<ContainerType>('/api/v1/admin/containers', { method: 'POST', token, body: draft })).data,
  updateContainer: async (container: ContainerType, draft: Omit<ContainerType, 'id' | 'code' | 'concurrencyToken'>) =>
    (await request<ContainerType>(`/api/v1/admin/containers/${container.id}`, { method: 'PUT', token, ifMatch: container.concurrencyToken, body: draft })).data,

  dataIssues: async (signal?: AbortSignal) => (await request<DataIssue[]>('/api/v1/admin/data-issues', { token, signal })).data,
  audit: async (filters?: { limit?: number; actorUserId?: string; entityType?: string; from?: string; to?: string; action?: string; correlationId?: string; entityId?: string; actor?: string }, signal?: AbortSignal) => {
    const search = new URLSearchParams()
    if (filters?.limit) search.set('limit', String(filters.limit))
    if (filters?.actorUserId) search.set('actorUserId', filters.actorUserId)
    if (filters?.entityType) search.set('entityType', filters.entityType)
    if (filters?.from) search.set('from', filters.from)
    if (filters?.to) search.set('to', filters.to)
    if (filters?.action) search.set('action', filters.action)
    if (filters?.correlationId) search.set('correlationId', filters.correlationId)
    if (filters?.entityId) search.set('entityId', filters.entityId)
    if (filters?.actor) search.set('actor', filters.actor)
    return (await request<AuditRecord[]>(`/api/v1/admin/audit-logs?${search}`, { token, signal })).data
  },

  rdsStatus: async (signal?: AbortSignal) => (await request<RdsConnectionStatus>('/api/v1/admin/rds/status', { token, signal })).data,
  rdsRobots: async (signal?: AbortSignal) => (await request<RdsRobotDetail[]>('/api/v1/admin/rds/robots', { token, signal })).data,
  rdsRobotAlarms: async (signal?: AbortSignal) => (await request<RdsRobotAlarm[]>('/api/v1/admin/rds/alarms', { token, signal })).data,
  rdsNavigationProfiles: async (signal?: AbortSignal) => (await request<RdsNavigationProfile[]>('/api/v1/admin/rds/navigation-profiles', { token, signal })).data,
  syncRdsWorkSites: async (navigationProfileId: string) =>
    (await request<RdsSyncResult>('/api/v1/admin/rds/worksites/sync', { method: 'POST', token, body: { navigationProfileId } })).data,
  rdsWorkSites: async (filters: { navigationProfileId: string; siteId?: string; area?: string; group?: string; type?: number; filled?: number; locked?: number; disabled?: number; syncFailed?: number }, signal?: AbortSignal) => {
    const search = new URLSearchParams({ navigationProfileId: filters.navigationProfileId })
    for (const [key, value] of Object.entries(filters)) if (key !== 'navigationProfileId' && value !== undefined && value !== '') search.set(key, String(value))
    return (await request<RdsWorkSite[]>(`/api/v1/admin/rds/worksites?${search}`, { token, signal })).data
  },
  rdsSlotMappings: async (layoutId?: string, signal?: AbortSignal) =>
    (await request<RdsSlotMapping[]>(`/api/v1/admin/rds/slot-mappings${layoutId ? `?layoutId=${layoutId}` : ''}`, { token, signal })).data,
  setRdsSlotMapping: async (slotId: string, rdsWorksiteId: string) =>
    (await request<RdsSlotMapping>(`/api/v1/admin/layout-slots/${slotId}/rds-mapping`, { method: 'PUT', token, body: { rdsWorksiteId } })).data,
  removeRdsSlotMapping: async (slotId: string) =>
    (await request<void>(`/api/v1/admin/layout-slots/${slotId}/rds-mapping`, { method: 'DELETE', token })).data,
  rdsTaskTemplates: async (signal?: AbortSignal) =>
    (await request<RdsTaskTemplate[]>('/api/v1/admin/rds/task-templates', { token, signal })).data,
  validateRdsTaskTemplate: async (taskTemplateId: string) =>
    (await request<RdsTaskTemplateValidation>('/api/v1/admin/rds/task-templates/validate', { method: 'POST', token, body: { taskTemplateId } })).data,
  productionEndPriorities: async (signal?: AbortSignal) =>
    (await request<ProductionEndPriority[]>('/api/v1/admin/production-end-priorities', { token, signal })).data,
  setProductionEndPriority: async (operationCode: string, priority: number | null) =>
    (await request<ProductionEndPriority[]>('/api/v1/admin/production-end-priorities', { method: 'PUT', token, body: { operationCode, priority } })).data,
  transportEvidence: async (requestId: string, signal?: AbortSignal) =>
    (await request<TransportEvidence>(`/api/v1/admin/transport-requests/${requestId}/evidence`, { token, signal })).data,
  reportTasks: async (filters: ReportWindow, signal?: AbortSignal) =>
    (await request<TaskReportPage>(`/api/v1/admin/reports/tasks?${reportSearch(filters)}`, { token, signal })).data,
  reportTask: async (requestId: string, signal?: AbortSignal) =>
    (await request<TaskReportDetail>(`/api/v1/admin/reports/tasks/${requestId}`, { token, signal })).data,
  reportSessions: async (filters: ReportWindow, signal?: AbortSignal) =>
    (await request<SessionReportPage>(`/api/v1/admin/reports/sessions?${reportSearch(filters)}`, { token, signal })).data,
  reportSummary: async (filters: ReportWindow, signal?: AbortSignal) =>
    (await request<OperationsSummary>(`/api/v1/admin/reports/summary?${reportSearch(filters)}`, { token, signal })).data,
  downloadReport: async (kind: 'tasks' | 'sessions', filters: ReportWindow, fileName: string) => {
    const response = await fetch(`/api/v1/admin/reports/${kind}/export.csv?${reportSearch(filters)}`, {
      headers: { Authorization: `Bearer ${token}`, Accept: 'text/csv' },
    })
    if (!response.ok) throw new ApiProblem({ status: response.status, title: 'Rapor indirilemedi', detail: 'CSV raporu oluşturulamadı.', code: 'REPORT_EXPORT_FAILED' })
    const blob = await response.blob()
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = fileName
    anchor.click()
    URL.revokeObjectURL(url)
  },

  commissioningReadiness: async (signal?: AbortSignal) =>
    (await request<VariantOperationReadiness[]>('/api/v1/admin/rds/commissioning/report', { token, signal })).data,
})

const reportSearch = (filters: ReportWindow) => {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(filters)) {
    if (value !== undefined && value !== '' && value !== false) search.set(key, String(value))
  }
  return search
}

export type AdminApi = ReturnType<typeof createAdminApi>
