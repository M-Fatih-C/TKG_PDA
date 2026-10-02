import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowUpRight, Bot, Boxes, CheckCircle2, Map, PackageOpen, RadioTower, RefreshCw, Smartphone, Users } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'
import { Button, EmptyState, ErrorState, LoadingState, PageHeader, Panel, PanelHeader, StatusPill, compactNumber, formatDate } from '../components/ui'

export function DashboardPage() {
  const api = useAdminApi()
  const client = useQueryClient()
  const [refreshing, setRefreshing] = useState(false)

  const users = useQuery({ queryKey: ['users'], queryFn: ({ signal }) => api.users(signal) })
  const devices = useQuery({ queryKey: ['devices'], queryFn: ({ signal }) => api.devices(signal) })
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const layouts = useQuery({ queryKey: ['layouts'], queryFn: ({ signal }) => api.layouts(undefined, signal) })
  const parts = useQuery({ queryKey: ['parts'], queryFn: ({ signal }) => api.parts(signal) })
  const containers = useQuery({ queryKey: ['containers'], queryFn: ({ signal }) => api.containers(signal) })
  const issues = useQuery({ queryKey: ['data-issues'], queryFn: ({ signal }) => api.dataIssues(signal) })
  const audit = useQuery({ queryKey: ['audit', 6], queryFn: ({ signal }) => api.audit({ limit: 6 }, signal) })
  const rdsStatus = useQuery({ queryKey: ['rds-status'], queryFn: ({ signal }) => api.rdsStatus(signal), refetchOnWindowFocus: false })
  const robots = useQuery({ queryKey: ['rds-robots'], queryFn: ({ signal }) => api.rdsRobots(signal), refetchOnWindowFocus: false })

  const all = [users, devices, machines, layouts, parts, containers, issues, audit, rdsStatus, robots]

  const refreshAll = async () => {
    setRefreshing(true)
    try {
      await Promise.all([
        client.invalidateQueries({ queryKey: ['users'] }),
        client.invalidateQueries({ queryKey: ['devices'] }),
        client.invalidateQueries({ queryKey: ['machines'] }),
        client.invalidateQueries({ queryKey: ['layouts'] }),
        client.invalidateQueries({ queryKey: ['parts'] }),
        client.invalidateQueries({ queryKey: ['containers'] }),
        client.invalidateQueries({ queryKey: ['data-issues'] }),
        client.invalidateQueries({ queryKey: ['audit'] }),
        client.invalidateQueries({ queryKey: ['rds-status'] }),
        client.invalidateQueries({ queryKey: ['rds-robots'] }),
      ])
    } finally {
      setRefreshing(false)
    }
  }

  const lastUpdated = Math.max(
    users.dataUpdatedAt || 0,
    devices.dataUpdatedAt || 0,
    machines.dataUpdatedAt || 0,
    layouts.dataUpdatedAt || 0,
    parts.dataUpdatedAt || 0,
    containers.dataUpdatedAt || 0,
    issues.dataUpdatedAt || 0,
    audit.dataUpdatedAt || 0,
    rdsStatus.dataUpdatedAt || 0,
    robots.dataUpdatedAt || 0,
  )

  if (all.some((query) => query.isLoading && !query.data)) return <LoadingState label="Yönetim özeti hazırlanıyor…" />
  const failure = all.find((query) => query.error)?.error
  if (failure) return <ErrorState error={failure} onRetry={refreshAll} />

  const highIssues = issues.data?.filter((issue) => issue.severity === 'HIGH').length ?? 0
  const onlineRobots = robots.data?.filter((r) => r.connectivity === 'ONLINE').length ?? 0
  const activeRobots = robots.data?.filter((r) => r.execution === 'RUNNING' || r.motion === 'MOVING').length ?? 0
  const totalRobots = robots.data?.length ?? 0

  const cards = [
    {
      label: 'Tanımlı Makine',
      value: machines.data?.filter((m) => m.isActive).length ?? 0,
      meta: `${machines.data?.length ?? 0} toplam makine`,
      icon: Boxes,
      to: '/machines',
      tone: 'normal',
    },
    {
      label: 'Yayındaki Layout',
      value: layouts.data?.filter((item) => item.status === 'PUBLISHED').length ?? 0,
      meta: `${layouts.data?.filter((item) => item.status === 'DRAFT').length ?? 0} draft sürüm`,
      icon: Map,
      to: '/layouts',
      tone: 'normal',
    },
    {
      label: 'Robot Filosu',
      value: totalRobots ? `${onlineRobots}/${totalRobots}` : '—',
      meta: totalRobots ? `${activeRobots} görevde · ${onlineRobots} çevrimiçi` : 'Bağlantı bekleniyor',
      icon: Bot,
      to: '/rds',
      tone: totalRobots > 0 && onlineRobots < totalRobots ? 'warning' : 'normal',
    },
    {
      label: 'Veri Kalite Konusu',
      value: issues.data?.length ?? 0,
      meta: `${highIssues} yüksek öncelikli`,
      icon: AlertTriangle,
      to: '/data-quality',
      tone: highIssues > 0 ? 'danger' : 'normal',
    },
  ]

  const formattedTime = lastUpdated > 0
    ? new Intl.DateTimeFormat('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date(lastUpdated))
    : '—'

  return (
    <div className="page">
      <PageHeader
        eyebrow="Operasyon & Sistem Özeti"
        title="Genel Bakış"
        description="Makineler, fiziksel yerleşimler, robot bağlantısı ve veri kalitesinin anlık durumu."
        actions={
          <div className="toolbar-group">
            <span className="last-sync-badge">Son güncelleme: {formattedTime}</span>
            <Button size="sm" variant="secondary" disabled={refreshing} onClick={refreshAll}>
              <RefreshCw size={14} className={refreshing ? 'spin' : ''} /> Yenile
            </Button>
          </div>
        }
      />

      <div className="stat-grid">
        {cards.map(({ label, value, meta, icon: Icon, to }) => (
          <Link className="stat-card" to={to} key={label}>
            <div>
              <span>{label}</span>
              <strong>{typeof value === 'number' ? compactNumber(value) : value}</strong>
              <small>{meta}</small>
            </div>
            <div className="stat-card__icon">
              <Icon />
            </div>
            <ArrowUpRight className="stat-card__arrow" size={17} />
          </Link>
        ))}
      </div>

      <div className="dashboard-grid">
        <Panel>
          <PanelHeader
            title="Sistem & Donanım Durumu"
            description="Aktif istemciler, RDS sunucusu ve filo bağlantısı"
            action={<Link className="text-link" to="/rds">RDS Detayı <ArrowUpRight size={15} /></Link>}
          />
          <div className="health-list">
            <div>
              <span><RadioTower /> RDS Entegrasyon Sunucusu</span>
              <StatusPill
                value={rdsStatus.data?.reachable ? 'CONNECTED' : rdsStatus.data?.enabled ? 'UNAVAILABLE' : 'DISABLED'}
                tone={rdsStatus.data?.reachable ? 'success' : 'warning'}
              />
            </div>
            <div>
              <span><Smartphone /> Aktif Saha PDA Cihazları</span>
              <strong>{devices.data?.filter((item) => item.type === 'PDA' && item.isActive).length ?? 0}</strong>
            </div>
            <div>
              <span><Users /> Aktif Operatör / Kullanıcı</span>
              <strong>{users.data?.filter((item) => item.isActive).length ?? 0}</strong>
            </div>
            <div>
              <span><Bot /> Görevdeki AGV Robotlar</span>
              <strong>{activeRobots}</strong>
            </div>
            <div>
              <span><PackageOpen /> Ölçüsü Eksik Kasalar</span>
              <strong>{containers.data?.filter((item) => item.lengthMm === null || item.widthMm === null || item.heightMm === null).length ?? 0}</strong>
            </div>
          </div>
        </Panel>

        <Panel>
          <PanelHeader
            title="Son Sistem Hareketleri"
            description="Audit geçmişinin son operasyonel kayıtları"
            action={<Link className="text-link" to="/audit">Tümünü gör <ArrowUpRight size={15} /></Link>}
          />
          {audit.data?.length ? (
            <div className="activity-list">
              {audit.data.map((item) => (
                <div key={item.id}>
                  <span className="activity-dot" />
                  <div>
                    <strong>{item.action.replaceAll('_', ' ')}</strong>
                    <small>{item.actorUsername ?? 'Sistem'} · {item.entityType}</small>
                  </div>
                  <time>{formatDate(item.createdAt)}</time>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState title="Audit kaydı yok" description="İlk yapılandırma değişikliği burada görünecek." />
          )}
        </Panel>
      </div>

      <Panel>
        <PanelHeader
          title="Müdahale ve Veri Doğrulama"
          description="Doğruluk kontrolü bekleyen lojistik ve konfigürasyon kayıtları"
          action={<Link className="text-link" to="/data-quality">Tümünü İncele <ArrowUpRight size={15} /></Link>}
        />
        {issues.data && issues.data.length > 0 ? (
          <div className="issue-strip">
            {issues.data.slice(0, 5).map((issue) => (
              <div key={`${issue.kind}-${issue.entityId}`}>
                <StatusPill value={issue.severity} />
                <strong>{issue.code}</strong>
                <span>{issue.message}</span>
              </div>
            ))}
          </div>
        ) : (
          <div className="issue-strip-clean">
            <CheckCircle2 size={24} className="text-success" />
            <div>
              <strong>Tüm kontroller normal</strong>
              <p>Müdahale gerektiren açık bir veri kalite veya konfigürasyon uyarısı bulunmuyor.</p>
            </div>
          </div>
        )}
      </Panel>
    </div>
  )
}
