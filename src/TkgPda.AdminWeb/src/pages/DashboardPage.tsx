import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowUpRight, Bot, Boxes, PackageOpen, RadioTower, RefreshCw, Smartphone, Users } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'
import { Button, EmptyState, ErrorState, InfoBox, LoadingState, PageHeader, Panel, PanelHeader, StatusPill, compactNumber, formatDate } from '../components/ui'
import { auditActionLabel, auditEntityLabel } from '../displayLabels'

export function DashboardPage() {
  const api = useAdminApi()
  const client = useQueryClient()
  const [refreshing, setRefreshing] = useState(false)

  const users = useQuery({ queryKey: ['users'], queryFn: ({ signal }) => api.users(signal) })
  const devices = useQuery({ queryKey: ['devices'], queryFn: ({ signal }) => api.devices(signal) })
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const containers = useQuery({ queryKey: ['containers'], queryFn: ({ signal }) => api.containers(signal) })
  const audit = useQuery({ queryKey: ['audit', 6], queryFn: ({ signal }) => api.audit({ limit: 6 }, signal) })
  const rdsStatus = useQuery({ queryKey: ['rds-status'], queryFn: ({ signal }) => api.rdsStatus(signal), refetchOnWindowFocus: false })
  const robots = useQuery({ queryKey: ['rds-robots'], queryFn: ({ signal }) => api.rdsRobots(signal), refetchOnWindowFocus: false })
  const alarms = useQuery({ queryKey: ['rds-alarms'], queryFn: ({ signal }) => api.rdsRobotAlarms(signal), refetchOnWindowFocus: false })

  const requiredQueries = [users, devices, machines, containers, audit]

  const refreshAll = async () => {
    setRefreshing(true)
    try {
      await Promise.all([
        client.invalidateQueries({ queryKey: ['users'] }),
        client.invalidateQueries({ queryKey: ['devices'] }),
        client.invalidateQueries({ queryKey: ['machines'] }),
        client.invalidateQueries({ queryKey: ['containers'] }),
        client.invalidateQueries({ queryKey: ['audit'] }),
        client.invalidateQueries({ queryKey: ['rds-status'] }),
        client.invalidateQueries({ queryKey: ['rds-robots'] }),
        client.invalidateQueries({ queryKey: ['rds-alarms'] }),
      ])
    } finally {
      setRefreshing(false)
    }
  }

  const lastUpdated = Math.max(
    users.dataUpdatedAt || 0,
    devices.dataUpdatedAt || 0,
    machines.dataUpdatedAt || 0,
    containers.dataUpdatedAt || 0,
    audit.dataUpdatedAt || 0,
    rdsStatus.dataUpdatedAt || 0,
    robots.dataUpdatedAt || 0,
    alarms.dataUpdatedAt || 0,
  )

  if (requiredQueries.some((query) => query.isLoading && !query.data)) return <LoadingState label="Yönetim özeti hazırlanıyor…" />
  const failure = requiredQueries.find((query) => query.error)?.error
  if (failure) return <ErrorState error={failure} onRetry={refreshAll} />

  const criticalAlarms = alarms.data?.filter((alarm) => alarm.severity === 'fatal' || alarm.severity === 'error').length ?? 0
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
      label: 'Aktif PDA',
      value: devices.data?.filter((item) => item.type === 'PDA' && item.isActive).length ?? 0,
      meta: `${devices.data?.length ?? 0} kayıtlı cihaz`,
      icon: Smartphone,
      to: '/devices',
      tone: 'normal',
    },
    {
      label: 'Robot Filosu',
      value: robots.error ? '—' : totalRobots ? `${onlineRobots}/${totalRobots}` : '—',
      meta: robots.error ? 'Robot durumu okunamadı' : totalRobots ? `${activeRobots} görevde · ${onlineRobots} çevrimiçi` : 'Bağlantı bekleniyor',
      icon: Bot,
      to: '/rds',
      tone: robots.error || (totalRobots > 0 && onlineRobots < totalRobots) ? 'warning' : 'normal',
    },
    {
      label: 'Kritik Robot Alarmı',
      value: alarms.error ? '—' : criticalAlarms,
      meta: alarms.error ? 'Alarm bilgisi okunamadı' : `${alarms.data?.length ?? 0} görünen alarm`,
      icon: AlertTriangle,
      to: '/rds',
      tone: alarms.error ? 'warning' : criticalAlarms > 0 ? 'danger' : 'normal',
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
        description="Aktif makineler, saha cihazları, robot filosu ve son denetim hareketleri."
        actions={
          <div className="toolbar-group">
            <span className="last-sync-badge">Son güncelleme: {formattedTime}</span>
            <Button size="sm" variant="secondary" disabled={refreshing} onClick={refreshAll}>
              <RefreshCw size={14} className={refreshing ? 'spin' : ''} /> Yenile
            </Button>
          </div>
        }
      />

      <InfoBox title="Genel bakış nasıl okunur?">Bu ekran anlık operasyon ve bağlantı özetidir. Uyarı görülen karta tıklayarak ilgili yönetim sayfasındaki ayrıntıya ulaşabilirsiniz; kartlar tek başına robot komutu oluşturmaz.</InfoBox>

      <div className="stat-grid">
        {cards.map(({ label, value, meta, icon: Icon, to, tone }) => (
          <Link className={`stat-card stat-card--${tone}`} to={to} key={label}>
            <div>
              <span>{label}</span>
              <strong>{typeof value === 'number' ? compactNumber(value) : value}</strong>
              <small>{meta}</small>
              {tone === 'danger' ? (
                <em className="stat-card__state">Kritik</em>
              ) : tone === 'warning' ? (
                <em className="stat-card__state">Dikkat</em>
              ) : null}
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
                value={rdsStatus.error ? 'UNAVAILABLE' : rdsStatus.data?.reachable ? 'CONNECTED' : rdsStatus.data?.enabled ? 'UNAVAILABLE' : 'DISABLED'}
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
              <span><PackageOpen /> Fiziksel Ölçüsü Netleşmemiş Kasa Tipleri</span>
              <strong>{containers.data?.filter((item) => item.lengthMm === null || item.widthMm === null || item.heightMm === null).length ?? 0}</strong>
            </div>
          </div>
        </Panel>

        <Panel>
          <PanelHeader
            title="Son Sistem Hareketleri"
            description="Denetim geçmişinin son operasyonel kayıtları"
            action={<Link className="text-link" to="/audit">Tümünü gör <ArrowUpRight size={15} /></Link>}
          />
          {audit.data?.length ? (
            <div className="activity-list">
              {audit.data.map((item) => (
                <div key={item.id}>
                  <span className="activity-dot" />
                  <div>
                    <strong>{auditActionLabel(item.action)}</strong>
                    <small>{item.actorUsername ?? 'Sistem'} · {auditEntityLabel(item.entityType)}</small>
                  </div>
                  <time>{formatDate(item.createdAt)}</time>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState title="Denetim kaydı yok" description="İlk yapılandırma değişikliği burada görünecek." />
          )}
        </Panel>
      </div>
    </div>
  )
}
