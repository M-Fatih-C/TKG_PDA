import {
  ClipboardList,
  FileText,
  DatabaseZap,
  LayoutDashboard,
  LogOut,
  Menu,
  RadioTower,
  ListChecks,
  ListOrdered,
  ShieldCheck,
  Smartphone,
  Users,
  X,
} from 'lucide-react'
import { useState, type PropsWithChildren } from 'react'
import { NavLink, useLocation } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { NotificationCenter } from './NotificationCenter'

const links = [
  { to: '/', label: 'Genel Bakış', icon: LayoutDashboard, end: true },
  { to: '/users', label: 'Kullanıcılar & Yetkiler', icon: Users },
  { to: '/devices', label: 'Cihazlar', icon: Smartphone },
  { to: '/machines', label: 'Makine & Varyantlar', icon: DatabaseZap },
  { to: '/reports', label: 'Operasyon Kayıtları & Raporlar', icon: FileText },
  { to: '/audit', label: 'Audit Geçmişi', icon: ClipboardList },
  { to: '/production-end-priorities', label: 'Üretim Bitiş Önceliği', icon: ListOrdered },
  { to: '/rds', label: 'RDS Entegrasyonu', icon: RadioTower },
  { to: '/commissioning', label: 'Devreye Alma Hazırlığı', icon: ListChecks },
]

export function AppShell({ children }: PropsWithChildren) {
  const [open, setOpen] = useState(false)
  const { session, logout } = useAuth()
  const location = useLocation()
  const current = links.find((link) => link.end ? location.pathname === link.to : location.pathname.startsWith(link.to))

  return (
    <div className="app-shell">
      <aside className={`sidebar ${open ? 'sidebar--open' : ''}`}>
        <div className="brand"><span className="brand__mark">TKG</span><div><strong>PDA</strong><small>Yönetim Merkezi</small></div><button className="sidebar__close" onClick={() => setOpen(false)} aria-label="Menüyü kapat"><X /></button></div>
        <div className="environment"><span /> Canlı Operasyon & Yönetim</div>
        <nav aria-label="Ana menü">
          {links.map(({ to, label, icon: Icon, end }) => (
            <NavLink key={to} to={to} end={end} onClick={() => setOpen(false)}><Icon size={19} /><span>{label}</span></NavLink>
          ))}
        </nav>
        <div className="sidebar__footer">
          <div className="user-chip"><span>{session?.user.fullName.slice(0, 2).toUpperCase()}</span><div><strong>{session?.user.fullName}</strong><small>{session?.user.username}</small></div></div>
          <button onClick={logout}><LogOut size={18} /> Güvenli çıkış</button>
        </div>
      </aside>
      {open && <button className="sidebar-scrim" aria-label="Menüyü kapat" onClick={() => setOpen(false)} />}
      <div className="workspace">
        <header className="topbar">
          <div className="topbar__left">
            <button className="menu-button" onClick={() => setOpen(true)} aria-label="Menüyü aç">
              <Menu />
            </button>
            <div>
              <small>TKG PDA / Admin Web</small>
              <strong>{current?.label ?? 'Yönetim'}</strong>
            </div>
          </div>
          <div className="topbar__right">
            <NotificationCenter />
            <span className="topbar__scope">
              <ShieldCheck size={16} /> Sistem Yöneticisi
            </span>
          </div>
        </header>
        <main>{children}</main>
      </div>
    </div>
  )
}
