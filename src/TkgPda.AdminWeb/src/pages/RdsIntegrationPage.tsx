import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Bot, Database, RefreshCw, Router, ShieldAlert } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { useAdminApi } from '../api/ApiContext'
import { isRobotExecutionActive } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, EmptyState, ErrorState, LoadingState, PageHeader, Panel, PanelHeader, SearchInput, SelectInput, StatusPill, TableFrame, formatDate } from '../components/ui'

function BatteryIndicator({ level, isCharging }: { level: number | null; isCharging?: boolean | null }) {
  if (level === null || level === undefined) return <span className="muted">—</span>
  const pct = Math.max(0, Math.min(100, Math.round(level <= 1.0 ? level * 100 : level)))
  const tone = pct > 50 ? 'good' : pct > 20 ? 'warn' : 'bad'
  return (
    <div className="battery">
      <div className="battery__track">
        <div className={`battery__fill battery__fill--${tone}`} style={{ width: `${pct}%` }} />
      </div>
      <span>{pct}%{isCharging ? ' ⚡' : ''}</span>
    </div>
  )
}

export function RdsIntegrationPage() {
  const api = useAdminApi()
  const toast = useToast()
  const client = useQueryClient()
  const [profileId, setProfileId] = useState('')
  const [search, setSearch] = useState('')
  const [type, setType] = useState('')
  const [busy, setBusy] = useState(false)
  const [autoRefreshMs, setAutoRefreshMs] = useState(0)
  const [validatingTemplate, setValidatingTemplate] = useState<string | null>(null)

  const status = useQuery({ queryKey: ['rds-status'], queryFn: ({ signal }) => api.rdsStatus(signal), refetchOnWindowFocus: false })
  const robots = useQuery({
    queryKey: ['rds-robots'],
    queryFn: ({ signal }) => api.rdsRobots(signal),
    refetchOnWindowFocus: false,
    refetchInterval: autoRefreshMs > 0 ? autoRefreshMs : false,
  })
  // Deliberately NOT polled. /admin/rds/robots and /admin/rds/alarms both resolve to
  // the same upstream GET /api/agv-report/core, so polling both doubled the RDS load
  // every cycle. The fleet snapshot above carries per-severity counts for the KPIs
  // and per-row indicators; this query only backs the detail table and refreshes on
  // explicit request.
  const robotAlarms = useQuery({
    queryKey: ['rds-robot-alarms'],
    queryFn: ({ signal }) => api.rdsRobotAlarms(signal),
    refetchOnWindowFocus: false,
  })
  const profiles = useQuery({ queryKey: ['rds-navigation-profiles'], queryFn: ({ signal }) => api.rdsNavigationProfiles(signal) })
  const mappings = useQuery({ queryKey: ['rds-mappings'], queryFn: ({ signal }) => api.rdsSlotMappings(undefined, signal) })
  const taskTemplates = useQuery({ queryKey: ['rds-task-templates'], queryFn: ({ signal }) => api.rdsTaskTemplates(signal) })
  const workSites = useQuery({
    queryKey: ['rds-worksites', profileId, type],
    queryFn: ({ signal }) => api.rdsWorkSites({ navigationProfileId: profileId, type: type === '' ? undefined : Number(type) }, signal),
    enabled: !!profileId,
  })

  useEffect(() => {
    if (!profileId && profiles.data?.length) setProfileId(profiles.data.find((item) => item.status === 'PUBLISHED')?.id ?? profiles.data[0].id)
  }, [profileId, profiles.data])

  const filtered = useMemo(() => (workSites.data ?? []).filter((site) => `${site.siteId} ${site.area ?? ''} ${site.groupName ?? ''}`.toLocaleLowerCase('tr').includes(search.toLocaleLowerCase('tr'))), [search, workSites.data])
  const selectedProfile = profiles.data?.find((item) => item.id === profileId)

  // Fleet alarm rollup derived from the single fleet snapshot, keeping SEER
  // severity buckets separate so notices never inflate the fault KPI.
  const fleetAlarms = useMemo(
    () =>
      (robots.data ?? []).reduce(
        (total, robot) => ({
          critical: total.critical + robot.alarms.critical,
          warning: total.warning + robot.alarms.warning,
          notice: total.notice + robot.alarms.notice,
        }),
        { critical: 0, warning: 0, notice: 0 },
      ),
    [robots.data],
  )
  const mappedCount = mappings.data?.filter((item) => item.rdsWorkSiteId).length ?? 0

  const sync = async () => {
    if (!profileId || !selectedProfile) return
    if (!window.confirm(`${selectedProfile.machineCode} / ${selectedProfile.code} profilinin şu anda RDS'te yüklü sahneyi temsil ettiğini onaylıyor musunuz? Bu işlem RDS'i yalnız okur.`)) return
    setBusy(true)
    try {
      const result = await api.syncRdsWorkSites(profileId)
      await Promise.all([
        client.invalidateQueries({ queryKey: ['rds-worksites'] }),
        client.invalidateQueries({ queryKey: ['rds-status'] }),
        client.invalidateQueries({ queryKey: ['rds-mappings'] }),
      ])
      toast.success('WorkSite aynası güncellendi', `${result.received} okundu · ${result.created} yeni · ${result.updated} güncel`)
    } catch (error) { toast.error(error, 'RDS senkronizasyonu tamamlanamadı') } finally { setBusy(false) }
  }

  const validateTemplate = async (id: string) => {
    setValidatingTemplate(id)
    try {
      const result = await api.validateRdsTaskTemplate(id)
      if (result.validated) toast.success('RDS task label doğrulandı', result.message)
      else toast.error(new Error(result.message), 'RDS task label bulunamadı')
    } catch (error) { toast.error(error, 'RDS task template doğrulanamadı') } finally { setValidatingTemplate(null) }
  }

  if (status.isLoading || profiles.isLoading || mappings.isLoading || taskTemplates.isLoading) return <LoadingState label="RDS okuma durumu hazırlanıyor…" />
  const failure = status.error ?? profiles.error ?? mappings.error ?? taskTemplates.error
  if (failure) return <ErrorState error={failure} onRetry={() => void Promise.all([status.refetch(), profiles.refetch(), mappings.refetch(), taskTemplates.refetch(), robots.refetch(), robotAlarms.refetch()])} />

  return <div className="page">
    <PageHeader eyebrow="RDS Entegrasyonu" title="RDS Entegrasyonu" description="Robot yürütme, canlı durum, alarm ve WorkSite eşleşmeleri." actions={<Button variant="secondary" onClick={() => void Promise.all([status.refetch(), robots.refetch(), robotAlarms.refetch()])}><RefreshCw size={16} /> Bağlantıyı doğrula</Button>} />
    <div className="warning-banner"><ShieldAlert size={20} /><div><strong>RDS değerleri dış sistem kanıtıdır.</strong><span>Bu ekran görev oluşturmaz, WorkSite durumunu değiştirmez ve robot hareketi başlatmaz. İsim benzerliği otomatik eşleme üretmez.</span></div></div>
    <div className="rds-summary">
      <Panel><PanelHeader title="RDS Bağlantısı" description="Sunucu tarafından güvenli biçimde gösterilen bağlantı adresi" action={<StatusPill value={status.data?.businessMessage?.toLowerCase().includes('simülasyon') || status.data?.baseAddressDisplaySafe?.toLowerCase().includes('yerel') ? 'SİMÜLASYON (AKTİF)' : status.data?.reachable ? 'CONNECTED' : status.data?.enabled ? 'UNAVAILABLE' : 'DISABLED'} tone={status.data?.businessMessage?.toLowerCase().includes('simülasyon') || status.data?.baseAddressDisplaySafe?.toLowerCase().includes('yerel') ? 'info' : status.data?.reachable ? 'success' : 'warning'} />} /><div className="rds-card-body"><code>{status.data?.baseAddressDisplaySafe ?? 'Yapılandırılmadı'}</code><span>Son başarılı okuma: {formatDate(status.data?.lastSuccessfulRead)}</span>{status.data?.businessMessage && <small>RDS: {status.data.businessStatus} · {status.data.businessMessage}</small>}{status.data?.errorCode && <small className="danger-text">{status.data.errorCode} · {status.data.errorMessage}</small>}</div></Panel>
      <Panel><PanelHeader title="Eşleme Durumu" description="TKG eşleme tablosu" action={<StatusPill value={`${mappedCount} EŞLENDİ`} tone="info" />} /><div className="rds-card-body"><strong>{mappings.data?.length ?? 0} layout bölgesi</strong><span>{(mappings.data?.length ?? 0) - mappedCount} bölge eşlenmemiş veya MACHINE referansıdır.</span></div></Panel>
    </div>

    <Panel>
      <PanelHeader
        title="Robot Filosu ve Canlı Telemetri"
        description="SEER RDSCore üzerinden okunan anlık filo konumu, batarya, harita ve operasyon durumu."
        action={
          <div className="refresh-control">
            <SelectInput
              aria-label="Otomatik yenileme"
              value={autoRefreshMs}
              onChange={(e) => setAutoRefreshMs(Number(e.target.value))}
            >
              <option value="0">Yenileme: Kapalı</option>
              <option value="5000">Yenileme: 5 sn</option>
              <option value="10000">Yenileme: 10 sn</option>
            </SelectInput>
            <Button variant="secondary" disabled={robots.isFetching} onClick={() => void Promise.all([robots.refetch(), robotAlarms.refetch()])}>
              <RefreshCw size={15} className={robots.isFetching ? 'spin' : ''} /> Yenile
            </Button>
          </div>
        }
      />
      {robots.isLoading ? (
        <LoadingState label="Robot filosu okunuyor…" />
      ) : robots.error ? (
        <ErrorState error={robots.error} onRetry={() => void robots.refetch()} />
      ) : robots.data?.length ? (
        <>
          <div className="fleet-summary">
            <div className="fleet-tile">
              <span>Toplam Robot</span>
              <strong>{robots.data.length}</strong>
            </div>
            <div className="fleet-tile fleet-tile--good">
              <span>Bağlantı &amp; Sevk</span>
              <strong>
                {robots.data.filter(r => r.connectivity === 'ONLINE').length} / {robots.data.length} Çevrimiçi
              </strong>
              <small>
                {robots.data.filter(r => r.dispatch === 'DISPATCHABLE').length} Sevk Edilebilir
              </small>
            </div>
            <div className={`fleet-tile${robots.data.some(r => r.isLocked === true) ? ' fleet-tile--warn' : ''}`}>
              <span>Kilit Durumu (Control / Lock)</span>
              <strong>
                {robots.data.filter(r => r.isLocked === true).length} Kilitli
              </strong>
              <small>
                {robots.data.filter(r => r.control === 'TKG_CORRELATED').length} TKG İlişkili · {robots.data.filter(r => r.control === 'EXTERNAL_OR_OTHER').length} Harici/Diğer · {robots.data.filter(r => r.control === 'UNKNOWN').length} Bilinmiyor
              </small>
            </div>
            <div className={`fleet-tile ${fleetAlarms.critical > 0 ? 'fleet-tile--bad' : 'fleet-tile--good'}`}>
              <span>Aktif Arıza (Fatal + Error)</span>
              <strong>{fleetAlarms.critical}</strong>
              <small>
                {fleetAlarms.warning} uyarı · {fleetAlarms.notice} bilgi notu (arıza sayılmaz)
              </small>
            </div>
          </div>
          <TableFrame>
            <thead>
              <tr>
                <th>Robot</th>
                <th>Bağlantı & Sevk</th>
                <th>Hareket & Yürütme</th>
                <th>Kontrol (Lock)</th>
                <th>Batarya</th>
                <th>Konum / İstasyon</th>
                <th>Harita Telemetrisi</th>
                <th>Alarm</th>
              </tr>
            </thead>
            <tbody>
              {robots.data.map((robot) => {
                // Activity comes from execution state alone. SEER keeps reporting the
                // previous order id after it FINISHES, so a retained activeOrderId is
                // not activity and must not suppress the pre-dispatch diagnostics.
                const isRunning = isRobotExecutionActive(robot)
                // A NavigationProfile belongs to Machine -> Variant, never to a robot.
                // Nothing here establishes which profile applies to THIS robot, so the
                // admin's selection is shown strictly as a manual diagnostic reference.
                const referenceMap = selectedProfile?.robotMapName
                const runtimeMap = robot.currentMap

                return (
                  <tr key={robot.robotId}>
                    <td>
                      <div className="identity-inline">
                        <Bot size={18} />
                        <div>
                          <strong>{robot.robotId}</strong>
                          {robot.model && <small className="table-subtitle">{robot.model}</small>}
                          {robot.rdsReportedPayloadLoaded !== undefined && robot.rdsReportedPayloadLoaded !== null && (
                            <small className="table-subtitle" title="SEER robot teknik telemetrisi (TKG LoadState ile ilişkisizdir)">
                              Yük Telemetrisi: {robot.rdsReportedPayloadLoaded ? 'YÜKLÜ (RDS)' : 'BOŞ (RDS)'}
                            </small>
                          )}
                        </div>
                      </div>
                    </td>
                    <td>
                      <div className="cell-stack">
                        <StatusPill
                          value={robot.connectivity}
                          tone={robot.connectivity === 'ONLINE' ? 'success' : robot.connectivity === 'OFFLINE' ? 'danger' : 'neutral'}
                        />
                        <StatusPill
                          value={robot.dispatch}
                          tone={robot.dispatch === 'DISPATCHABLE' ? 'success' : robot.dispatch === 'UNDISPATCHABLE' ? 'warning' : 'neutral'}
                        />
                        {robot.undispatchableReason && (
                          <small className="table-subtitle">{robot.undispatchableReason}</small>
                        )}
                      </div>
                    </td>
                    <td>
                      <div className="cell-stack">
                        <div className="cell-row">
                          <small className="cell-label">Hareket:</small>
                          <StatusPill
                            value={robot.motion ?? 'UNKNOWN'}
                            tone={robot.motion === 'MOVING' ? 'info' : robot.motion === 'BLOCKED' ? 'danger' : robot.motion === 'PAUSED' ? 'warning' : 'neutral'}
                          />
                        </div>
                        <div className="cell-row">
                          <small className="cell-label">Yürütme:</small>
                          <StatusPill
                            value={robot.execution ?? 'IDLE'}
                            tone={robot.execution === 'RUNNING' ? 'info' : robot.execution === 'ERROR' ? 'danger' : robot.execution === 'SUSPENDED' ? 'warning' : robot.execution === 'FINISHED' ? 'success' : 'neutral'}
                          />
                        </div>
                        {robot.activeOrderId && (
                          <small className="table-subtitle mono">Emir: {robot.activeOrderId}</small>
                        )}
                      </div>
                    </td>
                    <td>
                      <div className="cell-stack">
                        <StatusPill
                          value={robot.control}
                          tone={robot.control === 'TKG_CORRELATED' ? 'info' : robot.control === 'EXTERNAL_OR_OTHER' ? 'warning' : 'neutral'}
                        />
                        {robot.isLocked === true && robot.lockNickName && (
                          <small className="table-subtitle mono" title={robot.lockNickName}>
                            {robot.lockNickName.length > 14 ? `${robot.lockNickName.slice(0, 14)}…` : robot.lockNickName}
                          </small>
                        )}
                      </div>
                    </td>
                    <td>
                      <BatteryIndicator level={robot.batteryLevel} isCharging={robot.isCharging} />
                    </td>
                    <td>
                      <strong>{robot.currentStation ?? '—'}</strong>
                      {robot.lastStation && robot.lastStation !== robot.currentStation && (
                        <small className="table-subtitle">Önceki: {robot.lastStation}</small>
                      )}
                    </td>
                    <td>
                      <div className="cell-stack">
                        <div>
                          <small className="cell-label">Çalışma Zamanı: </small>
                          <code>{runtimeMap ?? '—'}</code>
                        </div>
                        {referenceMap && (
                          <div>
                            <small className="cell-label" title="Aşağıda elle seçilen profil; bu robotun yetkili profili değildir.">
                              Referans Profil Haritası:{' '}
                            </small>
                            <code>{referenceMap}</code>
                          </div>
                        )}
                        {isRunning ? (
                          referenceMap && runtimeMap !== referenceMap ? (
                            <StatusPill value="RDSCORE ROTA YÖNETİMLİ HARİTA" tone="neutral" />
                          ) : referenceMap ? (
                            <StatusPill value="SEVKLİ (AYNI HARİTA)" tone="neutral" />
                          ) : null
                        ) : referenceMap ? (
                          runtimeMap === referenceMap ? (
                            <StatusPill value="REFERANS PROFİL İLE AYNI" tone="neutral" />
                          ) : (
                            <StatusPill value="REFERANS PROFİLDEN FARKLI" tone="neutral" />
                          )
                        ) : null}
                      </div>
                    </td>
                    <td>
                      {/* Severity is preserved: a benign notice never renders as a fault. */}
                      {robot.alarms.critical > 0 ? (
                        <StatusPill value={`${robot.alarms.critical} ARIZA`} tone="danger" />
                      ) : robot.alarms.warning > 0 ? (
                        <StatusPill value={`${robot.alarms.warning} UYARI`} tone="warning" />
                      ) : robot.alarms.notice > 0 ? (
                        <StatusPill value={`${robot.alarms.notice} BİLGİ`} tone="neutral" />
                      ) : (
                        <StatusPill value="TEMİZ" tone="success" />
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </TableFrame>
        </>
      ) : (
        <EmptyState
          title="Bağlı robot bulunamadı"
          description="RDS üzerinden okunan aktif bir robot kaydı dönmedi. SEER RDSCore servisinin açık olduğunu kontrol edin."
        />
      )}
    </Panel>


    <Panel><PanelHeader title="Aktif Robot Alarmları" description="GET /api/agv-report/core üzerinden okunan anlık robot alarmları. Onaylama/temizleme veya RDS komutu içermez." action={<Button variant="secondary" onClick={() => void robotAlarms.refetch()}><RefreshCw size={16} /> Yenile</Button>} />{robotAlarms.isLoading ? <LoadingState label="Robot alarmları okunuyor…" /> : robotAlarms.error ? <ErrorState error={robotAlarms.error} onRetry={() => void robotAlarms.refetch()} /> : robotAlarms.data?.length ? <TableFrame><thead><tr><th>Robot</th><th>Severity</th><th>Code</th><th>Description</th><th>Occurrences</th><th>Time</th><th>Dispatchable</th></tr></thead><tbody>{robotAlarms.data.map((alarm, index) => <tr key={`${alarm.robotId}-${alarm.code}-${index}`}><td><strong>{alarm.robotId}</strong>{alarm.robotModel && <small className="table-subtitle">{alarm.robotModel}</small>}</td><td><StatusPill value={alarm.severity.toUpperCase()} tone={alarm.severity === 'fatal' || alarm.severity === 'error' ? 'danger' : alarm.severity === 'warning' ? 'warning' : 'neutral'} /></td><td><code>{alarm.code}</code></td><td>{alarm.description ?? '—'}</td><td>{alarm.occurrenceCount ?? '—'}</td><td>{alarm.occurredAt ?? '—'}</td><td>{alarm.dispatchable === null ? '—' : <StatusPill value={alarm.dispatchable ? 'YES' : `NO${alarm.undispatchableReason ? ` · ${alarm.undispatchableReason}` : ''}`} tone={alarm.dispatchable ? 'success' : 'warning'} />}</td></tr>)}</tbody></TableFrame> : <EmptyState title="Aktif robot alarmı yok" description="Şu anda RDS'ten okunan filo genelinde açık bir alarm bulunmuyor." />}</Panel>
    <Panel><PanelHeader title="WorkSite Senkronizasyonu" description="RDS'te o anda yüklü sahneyi temsil eden TKG navigation profile'ını admin açıkça seçmelidir." action={<Button disabled={!profileId || busy || !status.data?.enabled} onClick={() => void sync()}><Database size={16} /> Senkronize et</Button>} /><div className="rds-sync-controls"><SelectInput aria-label="Navigation profile" value={profileId} onChange={(event) => setProfileId(event.target.value)}><option value="">Profil seçin</option>{profiles.data?.map((profile) => <option key={profile.id} value={profile.id}>{profile.machineCode} · {profile.code} v{profile.version} · {profile.status}</option>)}</SelectInput><span><Router size={16} /> Fiziksel alan, slot veya harita eşleşmesi için yetkili profil seçilmelidir.</span></div></Panel>
    <Panel><PanelHeader title="RDS Task Templates" description="Task label ve açık input binding sözleşmesi. Doğrulama RDS'te görev oluşturmaz." action={<StatusPill value={`${taskTemplates.data?.length ?? 0} TEMPLATE`} tone="info" />} />{taskTemplates.data?.length ? <TableFrame><thead><tr><th>Varyant / Grup</th><th>Operasyon</th><th>RDS taskLabel</th><th>Input bindings</th><th>Canlı doğrulama</th></tr></thead><tbody>{taskTemplates.data.map((template) => { const bindings = Array.isArray(template.config.rdsInputBindings) ? template.config.rdsInputBindings : []; return <tr key={template.id}><td><strong>{template.variantCode}</strong><small className="table-subtitle">{template.productGroupCode ?? 'Varsayılan grup'}</small></td><td>{template.operation}</td><td><code>{template.rdsTaskLabel ?? 'Yapılandırılmadı'}</code></td><td><code>{bindings.map((item) => typeof item === 'object' && item !== null && 'param' in item ? String(item.param) : '?').join(', ') || 'Yapılandırılmadı'}</code></td><td><Button variant="secondary" disabled={!template.rdsTaskLabel || validatingTemplate === template.id || !status.data?.enabled} onClick={() => void validateTemplate(template.id)}><RefreshCw size={15} /> Doğrula</Button></td></tr>})}</tbody></TableFrame> : <EmptyState title="Task template bulunamadı" description="Dispatch için variant / product group / operation bağlamında açık bir template gereklidir." />}</Panel>
    <Panel><PanelHeader title="Mirrored WorkSite Browser" description="filled / locked / disabled / syncFailed değerleri RDS'in son okunan kanıtıdır." action={<StatusPill value={`${filtered.length} WORKSITE`} tone="neutral" />} /><div className="table-toolbar table-toolbar--split"><SearchInput value={search} onChange={setSearch} placeholder="siteId, area veya grup ara…" /><SelectInput aria-label="WorkSite tipi" value={type} onChange={(event) => setType(event.target.value)}><option value="">Tüm tipler</option><option value="1">Physical (1)</option><option value="0">Logical (0)</option></SelectInput></div>{!profileId ? <EmptyState title="Navigation profile seçin" description="WorkSite aynası profile göre ayrılır." /> : workSites.isLoading ? <LoadingState label="WorkSite aynası okunuyor…" /> : workSites.error ? <ErrorState error={workSites.error} onRetry={() => void workSites.refetch()} /> : filtered.length ? <TableFrame><thead><tr><th>siteId</th><th>Alan / Grup</th><th>Tip</th><th>filled</th><th>locked</th><th>disabled</th><th>syncFailed</th><th>content</th><th>Readiness</th></tr></thead><tbody>{filtered.map((site) => <tr key={site.id}><td><strong>{site.siteId}</strong><small className="table-subtitle">{formatDate(site.lastSyncAt)}</small></td><td>{site.area ?? '—'}<small className="table-subtitle">{site.groupName ?? '—'}</small></td><td><StatusPill value={site.type === 1 ? 'PHYSICAL' : site.type === 0 ? 'LOGICAL' : 'UNKNOWN'} tone={site.type === 1 ? 'info' : 'neutral'} /></td><td>{site.filled ?? '—'}</td><td>{site.locked ?? '—'}</td><td>{site.disabled ?? '—'}</td><td>{site.syncFailed ?? '—'}</td><td><code>{site.content === null || site.content === '' ? '∅' : site.content}</code></td><td><StatusPill value={site.executionReady ? 'READY' : 'NOT READY'} tone={site.executionReady ? 'success' : 'warning'} /></td></tr>)}</tbody></TableFrame> : <EmptyState title="WorkSite bulunamadı" description="Seçilen profil ve filtreler için aynalanmış kayıt yok." />}</Panel>
  </div>
}
