import { useQuery } from '@tanstack/react-query'
import { AlertCircle, AlertTriangle, Bell, CheckCheck, ExternalLink, Info, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'

export interface NotificationItem {
  id: string
  title: string
  message: string
  severity: 'danger' | 'warning' | 'info'
  link?: string
  source: string
}

export function NotificationCenter() {
  const api = useAdminApi()
  const [open, setOpen] = useState(false)
  const [readIds, setReadIds] = useState<Set<string>>(() => {
    try {
      const stored = sessionStorage.getItem('tkg_admin_read_notifications')
      return stored ? new Set(JSON.parse(stored)) : new Set()
    } catch {
      return new Set()
    }
  })
  const popoverRef = useRef<HTMLDivElement>(null)

  // Persist read notification IDs in session so page navigation doesn't reset unread status
  const markAsRead = (id: string) => {
    setReadIds((prev) => {
      const next = new Set(prev)
      next.add(id)
      try {
        sessionStorage.setItem('tkg_admin_read_notifications', JSON.stringify(Array.from(next)))
      } catch {
        // ignore storage errors
      }
      return next
    })
  }

  const markAllAsRead = (ids: string[]) => {
    setReadIds((prev) => {
      const next = new Set(prev)
      ids.forEach((id) => next.add(id))
      try {
        sessionStorage.setItem('tkg_admin_read_notifications', JSON.stringify(Array.from(next)))
      } catch {
        // ignore storage errors
      }
      return next
    })
  }

  const issuesQuery = useQuery({
    queryKey: ['data-issues'],
    queryFn: ({ signal }) => api.dataIssues(signal),
    staleTime: 30_000,
    refetchInterval: 30_000,
  })

  const rdsStatusQuery = useQuery({
    queryKey: ['rds-status'],
    queryFn: ({ signal }) => api.rdsStatus(signal),
    staleTime: 15_000,
    refetchInterval: 15_000,
  })

  const robotsQuery = useQuery({
    queryKey: ['rds-robots'],
    queryFn: ({ signal }) => api.rdsRobots(signal),
    staleTime: 15_000,
    refetchInterval: 15_000,
  })

  // Close dropdown on outside click
  useEffect(() => {
    if (!open) return
    const handleClick = (e: MouseEvent) => {
      if (popoverRef.current && !popoverRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', handleClick)
    return () => document.removeEventListener('mousedown', handleClick)
  }, [open])

  // Derive notifications from current live data
  const notifications = useMemo<NotificationItem[]>(() => {
    const items: NotificationItem[] = []

    // 1. Critical Data Quality Issues
    const highIssues = (issuesQuery.data ?? []).filter((i) => i.severity === 'HIGH')
    highIssues.forEach((issue) => {
      items.push({
        id: `issue:${issue.kind}:${issue.entityId}:${issue.code}`,
        title: `Veri Kalite Uyarısı: ${issue.code}`,
        message: issue.message,
        severity: 'danger',
        source: 'Ana Veri',
      })
    })

    // 2. RDS Connection issues
    if (rdsStatusQuery.data) {
      if (!rdsStatusQuery.data.reachable && rdsStatusQuery.data.enabled) {
        items.push({
          id: 'rds:unreachable',
          title: 'RDS Bağlantı Kesintisi',
          message: rdsStatusQuery.data.errorMessage || 'SEER RDS sunucusuna erişilemiyor.',
          severity: 'danger',
          link: '/rds',
          source: 'RDS Altyapısı',
        })
      } else if (rdsStatusQuery.data.errorCode) {
        items.push({
          id: `rds:error:${rdsStatusQuery.data.errorCode}`,
          title: `RDS Durum Bildirimi (${rdsStatusQuery.data.errorCode})`,
          message: rdsStatusQuery.data.errorMessage || rdsStatusQuery.data.businessMessage || '',
          severity: 'warning',
          link: '/rds',
          source: 'RDS Altyapısı',
        })
      }
    }

    // 3. Robot alarms & offline status
    const robotList = robotsQuery.data ?? []
    robotList.forEach((robot) => {
      if (robot.alarms.critical > 0) {
        items.push({
          id: `robot:alarm:${robot.robotId}`,
          title: `Kritik Robot Arızası: ${robot.robotId}`,
          message: `${robot.alarms.critical} adet kritik arıza alarmı mevcut.`,
          severity: 'danger',
          link: '/rds',
          source: 'Robot Filosu',
        })
      }
      if (robot.connectivity === 'OFFLINE') {
        items.push({
          id: `robot:offline:${robot.robotId}`,
          title: `Robot Çevrimdışı: ${robot.robotId}`,
          message: `${robot.robotId} AGV bağlantısı kesildi.`,
          severity: 'warning',
          link: '/rds',
          source: 'Robot Filosu',
        })
      }
    })

    return items
  }, [issuesQuery.data, rdsStatusQuery.data, robotsQuery.data])

  const unreadItems = useMemo(
    () => notifications.filter((n) => !readIds.has(n.id)),
    [notifications, readIds],
  )
  const unreadCount = unreadItems.length

  // Live status timestamp & staleness indicator
  const lastUpdated = Math.max(
    issuesQuery.dataUpdatedAt || 0,
    rdsStatusQuery.dataUpdatedAt || 0,
    robotsQuery.dataUpdatedAt || 0,
  )
  const isStale = lastUpdated > 0 && Date.now() - lastUpdated > 45_000
  const isError = Boolean(issuesQuery.error || rdsStatusQuery.error || robotsQuery.error)

  const timeString = lastUpdated > 0
    ? new Intl.DateTimeFormat('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date(lastUpdated))
    : null

  return (
    <div className="topbar-status-area" ref={popoverRef}>
      {/* Live sync heartbeat */}
      <div className={`status-indicator ${isError || isStale ? 'status-indicator--stale' : 'status-indicator--live'}`}>
        <span className="status-indicator__dot" />
        <span className="status-indicator__text">
          {isError ? 'Bağlantı hatası' : isStale ? 'Veri güncel değil' : timeString ? `Canlı · ${timeString}` : 'Bağlı'}
        </span>
      </div>

      {/* Notification Bell Button */}
      <div className="notification-bell-wrapper">
        <button
          className={`notification-bell-btn ${open ? 'active' : ''}`}
          onClick={() => setOpen(!open)}
          aria-label={unreadCount > 0 ? `${unreadCount} okunmamış bildirim` : 'Bildirimler'}
          title="Sistem Bildirimleri"
        >
          <Bell size={18} />
          {unreadCount > 0 && (
            <span className="notification-badge">
              {unreadCount > 9 ? '9+' : unreadCount}
            </span>
          )}
        </button>

        {/* Notification Popover */}
        {open && (
          <div className="notification-popover" role="dialog" aria-label="Sistem Bildirimleri">
            <div className="notification-popover__header">
              <div>
                <strong>Bildirimler &amp; Uyarılar</strong>
                <small>{notifications.length} aktif durum · {unreadCount} okunmamış</small>
              </div>
              {unreadCount > 0 && (
                <button
                  className="notification-popover__mark-read"
                  onClick={() => markAllAsRead(notifications.map((n) => n.id))}
                >
                  <CheckCheck size={14} /> Tümünü okundu say
                </button>
              )}
            </div>

            <div className="notification-popover__list">
              {notifications.length === 0 ? (
                <div className="notification-popover__empty">
                  <span className="status status--success">Normal</span>
                  <p>Müdahale gerektiren bir durum yok.</p>
                  <small>Veri kalitesi, RDS ve robot filosu kontrolleri temiz.</small>
                </div>
              ) : (
                notifications.map((item) => {
                  const isRead = readIds.has(item.id)
                  return (
                    <div
                      key={item.id}
                      className={`notification-card notification-card--${item.severity} ${isRead ? 'notification-card--read' : ''}`}
                    >
                      <div className="notification-card__icon">
                        {item.severity === 'danger' ? (
                          <AlertCircle size={17} />
                        ) : item.severity === 'warning' ? (
                          <AlertTriangle size={17} />
                        ) : (
                          <Info size={17} />
                        )}
                      </div>
                      <div className="notification-card__content">
                        <div className="notification-card__top">
                          <span className="notification-card__source">{item.source}</span>
                          {!isRead && (
                            <button
                              className="notification-card__dismiss"
                              onClick={() => markAsRead(item.id)}
                              title="Okundu olarak işaretle"
                              aria-label="Okundu olarak işaretle"
                            >
                              <X size={13} />
                            </button>
                          )}
                        </div>
                        <strong>{item.title}</strong>
                        <p>{item.message}</p>
                        {item.link && (
                          <Link
                            to={item.link}
                            className="notification-card__link"
                            onClick={() => setOpen(false)}
                          >
                            İncele <ExternalLink size={12} />
                          </Link>
                        )}
                      </div>
                    </div>
                  )
                })
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
