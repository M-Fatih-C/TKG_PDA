import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'
import type { ReportWindow, TaskReportRow } from '../api/types'
import { Button, EmptyState, ErrorState, Field, LoadingState, PageHeader, Panel, SelectInput, StatusPill, TableFrame, TextInput } from '../components/ui'

type Tab = 'tasks' | 'sessions' | 'summary' | 'failures'
type Preset = 'today' | '7' | '30' | 'month' | 'custom'

const presets: { id: Preset; label: string }[] = [
  { id: 'today', label: 'Bugün' },
  { id: '7', label: 'Son 7 gün' },
  { id: '30', label: 'Son 30 gün' },
  { id: 'month', label: 'Bu ay' },
  { id: 'custom', label: 'Özel tarih aralığı' },
]

export function ReportsPage() {
  const api = useAdminApi()
  const [tab, setTab] = useState<Tab>('tasks')
  const [preset, setPreset] = useState<Preset>('today')
  const [range, setRange] = useState<ReportWindow>(() => period('today'))
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<string | null>(null)
  const filters = { ...range, page, pageSize: 50 }
  const tasks = useQuery({ queryKey: ['reports', 'tasks', filters], queryFn: ({ signal }) => api.reportTasks(filters, signal), enabled: tab === 'tasks' })
  const sessions = useQuery({ queryKey: ['reports', 'sessions', filters], queryFn: ({ signal }) => api.reportSessions(filters, signal), enabled: tab === 'sessions' })
  const summary = useQuery({ queryKey: ['reports', 'summary', range], queryFn: ({ signal }) => api.reportSummary(range, signal), enabled: tab === 'summary' || tab === 'failures' })
  const detail = useQuery({ queryKey: ['reports', 'task', selected], queryFn: ({ signal }) => api.reportTask(selected!, signal), enabled: selected !== null })
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const devices = useQuery({ queryKey: ['devices'], queryFn: ({ signal }) => api.devices(signal) })

  const applyPreset = (next: Preset) => {
    setPreset(next)
    setPage(1)
    if (next !== 'custom') setRange(period(next))
  }
  const patch = (change: Partial<ReportWindow>) => { setPage(1); setRange((current) => ({ ...current, ...change })) }

  return (
    <div className="page reports-page">
      <div className="print-only">
        <h1>Operasyon Kayıtları & Raporlar</h1>
        <p>{stamp(range.from ?? null)} — {stamp(range.to ?? null)}</p>
        <p>Oluşturulma: {stamp(new Date().toISOString())}</p>
      </div>
      <PageHeader
        eyebrow="Operasyon"
        title="Operasyon Kayıtları & Raporlar"
        description="Taşıma görevleri, operatör oturumları ve operasyonel dönem analizleri."
        actions={<div className="no-print"><Button variant="secondary" onClick={() => void api.downloadReport(tab === 'sessions' ? 'sessions' : 'tasks', range, tab === 'sessions' ? 'TKG_Oturum_Raporu.xlsx' : 'TKG_Yonetim_Raporu.xlsx')}>Excel indir</Button><Button variant="ghost" onClick={() => window.print()}>Yazdır</Button></div>}
      />
      <div className="no-print report-toolbar">
        {presets.map((item) => <Button key={item.id} size="sm" variant={preset === item.id ? 'primary' : 'secondary'} onClick={() => applyPreset(item.id)}>{item.label}</Button>)}
        {preset === 'custom' && <>
          <Field label="Başlangıç"><TextInput type="date" aria-label="Başlangıç" onChange={(event) => patch({ from: localStart(event.target.value) })} /></Field>
          <Field label="Bitiş"><TextInput type="date" aria-label="Bitiş" onChange={(event) => patch({ to: localNextDay(event.target.value) })} /></Field>
        </>}
        <Field label="Makine"><SelectInput aria-label="Makine" value={range.machineId ?? ''} onChange={(event) => patch({ machineId: event.target.value || undefined })}><option value="">Tüm makineler</option>{(machines.data ?? []).map((machine) => <option key={machine.id} value={machine.id}>{machine.code}</option>)}</SelectInput></Field>
        <Field label="Sicil"><TextInput aria-label="Sicil" value={range.employeeNo ?? ''} onChange={(event) => patch({ employeeNo: event.target.value })} /></Field>
        <Field label="Bilgisayar"><SelectInput aria-label="Bilgisayar" value={range.deviceId ?? ''} onChange={(event) => patch({ deviceId: event.target.value || undefined })}><option value="">Tüm bilgisayarlar</option>{(devices.data ?? []).map((device) => <option key={device.id} value={device.id}>{device.code}</option>)}</SelectInput></Field>
        {tab === 'tasks' && <>
          <Field label="Durum"><SelectInput aria-label="Durum" value={range.status ?? ''} onChange={(event) => patch({ status: event.target.value || undefined })}><option value="">Tüm durumlar</option><option value="COMPLETED">Başarıyla tamamlandı</option><option value="FAILED">Hata ile sonlandı</option><option value="CANCELLED">İptal edildi</option><option value="RUNNING">Robot çalışıyor</option><option value="SENT_TO_RDS">RDS'ye gönderildi</option></SelectInput></Field>
          <Field label="İşlem"><SelectInput aria-label="İşlem" value={range.operation ?? ''} onChange={(event) => patch({ operation: event.target.value || undefined })}><option value="">Tüm işlemler</option><option value="BRING_FULL">Malzeme Getir</option><option value="BRING_EMPTY">Boş Kasa Getir</option><option value="REMOVE_FULL">Dolu Kasa Götür</option><option value="REMOVE_EMPTY">Boş Kasa Götür</option></SelectInput></Field>
          <Field label="Parça"><TextInput aria-label="Parça" value={range.part ?? ''} onChange={(event) => patch({ part: event.target.value })} /></Field>
          <Field label="Talep no"><TextInput aria-label="Talep no" value={range.requestNo ?? ''} onChange={(event) => patch({ requestNo: event.target.value })} /></Field>
          <Field label="Robot"><TextInput aria-label="Robot" value={range.agvId ?? ''} onChange={(event) => patch({ agvId: event.target.value })} /></Field>
        </>}
        {tab === 'sessions' && <>
          <Field label="Oturum"><SelectInput aria-label="Oturum durumu" value={range.activity ?? ''} onChange={(event) => patch({ activity: event.target.value || undefined })}><option value="">Tümü</option><option value="active">Aktif</option><option value="ended">Sonlanmış</option></SelectInput></Field>
          <Field label="Çıkış nedeni"><SelectInput aria-label="Çıkış nedeni" value={range.endReason ?? ''} onChange={(event) => patch({ endReason: event.target.value || undefined })}><option value="">Tümü</option><option value="USER_LOGOUT">Kullanıcı çıkışı</option><option value="LEASE_EXPIRED">Oturum süresi doldu</option><option value="TOKEN_EXPIRED">Oturum anahtarının süresi doldu</option><option value="REPLACED_SAME_DEVICE_LOGIN">Aynı cihazda yeni giriş</option></SelectInput></Field>
        </>}
      </div>
      <div className="report-tabs no-print" role="tablist">
        <button role="tab" aria-selected={tab === 'tasks'} onClick={() => setTab('tasks')}>Görev Geçmişi</button>
        <button role="tab" aria-selected={tab === 'sessions'} onClick={() => setTab('sessions')}>Kullanıcı Oturumları</button>
        <button role="tab" aria-selected={tab === 'summary'} onClick={() => setTab('summary')}>Operasyon Özeti</button>
        <button role="tab" aria-selected={tab === 'failures'} onClick={() => setTab('failures')}>Hata / İptal Analizi</button>
        <Link to="/audit">Teknik Denetim</Link>
      </div>
      {tab === 'tasks' && <TaskSection query={tasks} page={page} onPage={setPage} onOpen={setSelected} />}
      {tab === 'sessions' && <SessionSection query={sessions} page={page} onPage={setPage} />}
      {tab === 'summary' && <SummarySection query={summary} />}
      {tab === 'failures' && <FailureSection query={summary} />}
      {selected && detail.data && (
        <Panel className="report-timeline">
          <h2>Talep {detail.data.task.requestNo}</h2>
          <ol>{detail.data.timeline.map((entry) => <li key={`${entry.at}-${entry.title}`}><strong>{stamp(entry.at)}</strong> — {entry.title}{entry.actorLine && <small>{entry.actorLine}</small>}</li>)}</ol>
          <p>Toplam: {duration(detail.data.task.totalSeconds)} · Depo yanıtı: {duration(detail.data.task.warehouseResponseSeconds)} · Robot: {duration(detail.data.task.robotExecutionSeconds)}</p>
          <details className="no-print"><summary>Teknik Ayrıntılar</summary><p>Talep kimliği: {detail.data.task.requestId}</p><p>İlişkilendirme kimliği: {detail.data.task.correlationId}</p><Link to={`/audit?correlationId=${detail.data.task.correlationId}&entityId=${detail.data.task.requestId}`}>Teknik denetime git</Link></details>
          <Button className="no-print" variant="ghost" onClick={() => setSelected(null)}>Kapat</Button>
        </Panel>
      )}
    </div>
  )
}

function TaskSection({ query, page, onPage, onOpen }: { query: { isLoading: boolean; error: unknown; data?: { items: TaskReportRow[]; total: number; pageSize: number }; refetch: () => void }; page: number; onPage: (page: number) => void; onOpen: (id: string) => void }) {
  if (query.isLoading) return <LoadingState label="Görev geçmişi yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => query.refetch()} />
  const data = query.data!
  if (!data.items.length) return <EmptyState title="Bu dönemde görev yok" description="Tarih aralığını veya filtreleri değiştirin." />
  return <Panel><TableFrame><thead><tr><th>Talep No</th><th>Zaman</th><th>Kullanıcı</th><th>Bilgisayar</th><th>Makine</th><th>İşlem</th><th>Parça</th><th>Durum</th><th>Robot</th></tr></thead><tbody>
    {data.items.map((row) => <tr key={row.requestId} className="clickable-row" onClick={() => onOpen(row.requestId)}><td>{row.requestNo}</td><td>{stamp(row.createdAt)}</td><td>{row.requestedByName ?? 'Sistem'}<small className="table-subtitle">{row.employeeNo ? `Sicil ${row.employeeNo}` : ''}</small></td><td>{row.deviceCode ?? 'Bilgi yok'}</td><td>{row.machineCode}</td><td>{row.operationLabel}</td><td>{row.partCode ?? '—'}</td><td><StatusPill value={row.statusLabel} tone={tone(row.status)} /></td><td>{row.agvId ?? '—'}</td></tr>)}
  </tbody></TableFrame><Pager page={page} total={data.total} pageSize={data.pageSize} onPage={onPage} /></Panel>
}

function SessionSection({ query, page, onPage }: { query: { isLoading: boolean; error: unknown; data?: { items: { sessionId: string; loginAt: string; endedAt: string | null; durationSeconds: number | null; fullName: string; employeeNo: string | null; deviceCode: string; machineState: string; machineCode: string | null; statusLabel: string }[]; blockedAttempts: { at: string; message: string; employeeNo: string | null }[]; total: number; pageSize: number }; refetch: () => void }; page: number; onPage: (page: number) => void }) {
  if (query.isLoading) return <LoadingState label="Oturumlar yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => query.refetch()} />
  const data = query.data!
  return <Panel>
    {!data.items.length ? <EmptyState title="Bu dönemde oturum yok" description="Tarih aralığını veya sicil filtresini değiştirin." /> : <TableFrame><thead><tr><th>Giriş</th><th>Çıkış</th><th>Süre</th><th>Kullanıcı</th><th>Bilgisayar</th><th>Makine</th><th>Durum</th></tr></thead><tbody>
      {data.items.map((row) => <tr key={row.sessionId}><td>{stamp(row.loginAt)}</td><td>{row.endedAt ? stamp(row.endedAt) : '—'}</td><td>{duration(row.durationSeconds)}</td><td>{row.fullName}<small className="table-subtitle">{row.employeeNo ? `Sicil ${row.employeeNo}` : ''}</small></td><td>{row.deviceCode}</td><td>{machineText(row.machineState, row.machineCode)}</td><td><StatusPill value={row.statusLabel} tone={row.statusLabel === 'Aktif' ? 'info' : row.statusLabel === 'Kullanıcı çıkışı' ? 'neutral' : 'warning'} /></td></tr>)}
    </tbody></TableFrame>}
    {data.blockedAttempts.length > 0 && <div><h2>Engellenen ikinci girişler</h2><ul>{data.blockedAttempts.map((item) => <li key={`${item.at}-${item.employeeNo}`}>{stamp(item.at)} — {item.message}</li>)}</ul></div>}
    <Pager page={page} total={data.total} pageSize={data.pageSize} onPage={onPage} />
  </Panel>
}

function SummarySection({ query }: { query: { isLoading: boolean; error: unknown; data?: { total: number; completed: number; failed: number; cancelled: number; active: number; successRate: number | null; averageTotalSeconds: number | null; averageWarehouseSeconds: number | null; averageRobotSeconds: number | null; byMachine: { key: string; label: string; count: number }[]; byOperation: { label: string; count: number }[] }; refetch: () => void } }) {
  if (query.isLoading) return <LoadingState label="Özet yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => query.refetch()} />
  const data = query.data!
  return <div className="report-summary">
    <article><span>Toplam görev</span><strong>{data.total}</strong></article>
    <article><span>Tamamlanan</span><strong>{data.completed}</strong></article>
    <article><span>Hata</span><strong>{data.failed}</strong></article>
    <article><span>İptal</span><strong>{data.cancelled}</strong></article>
    <article><span>Aktif</span><strong>{data.active}</strong></article>
    <article><span>Başarı oranı</span><strong>{data.successRate === null ? 'Bilgi yok' : `${Math.round(data.successRate * 100)}%`}</strong></article>
    <article><span>Ortalama toplam</span><strong>{duration(data.averageTotalSeconds === null ? null : Math.round(data.averageTotalSeconds))}</strong></article>
    <article><span>Ortalama depo</span><strong>{duration(data.averageWarehouseSeconds === null ? null : Math.round(data.averageWarehouseSeconds))}</strong></article>
    <article><span>Ortalama robot</span><strong>{duration(data.averageRobotSeconds === null ? null : Math.round(data.averageRobotSeconds))}</strong></article>
    <Panel><h2>Makine</h2><ul>{data.byMachine.map((row) => <li key={row.key}>{row.label}: {row.count}</li>)}</ul></Panel>
    <Panel><h2>İşlem</h2><ul>{data.byOperation.map((row) => <li key={row.label}>{row.label}: {row.count}</li>)}</ul></Panel>
  </div>
}

function FailureSection({ query }: { query: { isLoading: boolean; error: unknown; data?: { topFailures: { key: string; count: number }[]; failed: number; cancelled: number }; refetch: () => void } }) {
  if (query.isLoading) return <LoadingState label="Hata analizi yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => query.refetch()} />
  const data = query.data!
  if (!data.topFailures.length) return <EmptyState title="Bu dönemde hata kodu yok" description="Başarısız görevler burada hata koduyla listelenir." />
  return <Panel><h2>Hata kodları</h2><p>{data.failed} hata, {data.cancelled} iptal</p><ul>{data.topFailures.map((row) => <li key={row.key}>{row.key}: {row.count}</li>)}</ul></Panel>
}

function Pager({ page, total, pageSize, onPage }: { page: number; total: number; pageSize: number; onPage: (page: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  if (pages <= 1) return null
  return <div className="no-print report-pager"><Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>Önceki</Button><span>{page} / {pages}</span><Button size="sm" variant="secondary" disabled={page >= pages} onClick={() => onPage(page + 1)}>Sonraki</Button></div>
}

function tone(status: string): 'success' | 'danger' | 'warning' | 'info' {
  if (status === 'COMPLETED') return 'success'
  if (status === 'FAILED') return 'danger'
  if (status === 'CANCELLED') return 'warning'
  if (status === 'RUNNING' || status === 'SENT_TO_RDS') return 'info'
  return 'warning'
}

function machineText(state: string, code: string | null) {
  if (state === 'UNKNOWN') return 'Geçmiş makine bilgisi kayıtlı değil'
  if (state === 'UNBOUND') return 'Bağlı makine yok'
  return code ?? 'Bilgi yok'
}

function duration(seconds: number | null) {
  if (seconds === null || seconds === undefined) return 'Bilgi yok'
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const rest = seconds % 60
  return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`
}

function stamp(value: string | null) {
  if (!value) return 'Bilgi yok'
  return new Intl.DateTimeFormat('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date(value))
}

function period(preset: Exclude<Preset, 'custom'>): ReportWindow {
  const end = startOfDay(new Date())
  end.setDate(end.getDate() + 1)
  const start = startOfDay(new Date())
  if (preset === '7') start.setDate(start.getDate() - 6)
  if (preset === '30') start.setDate(start.getDate() - 29)
  if (preset === 'month') start.setDate(1)
  return { from: start.toISOString(), to: end.toISOString() }
}

function startOfDay(value: Date) {
  const copy = new Date(value)
  copy.setHours(0, 0, 0, 0)
  return copy
}

function localStart(day: string) {
  return day ? new Date(`${day}T00:00:00`).toISOString() : undefined
}

function localNextDay(day: string) {
  if (!day) return undefined
  const date = new Date(`${day}T00:00:00`)
  date.setDate(date.getDate() + 1)
  return date.toISOString()
}
