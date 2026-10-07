import { Navigate, Outlet, Route, Routes, useLocation } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import { AppShell } from './components/AppShell'
import { AuditPage } from './pages/AuditPage'
import { CommissioningReadinessPage } from './pages/CommissioningReadinessPage'
import { DashboardPage } from './pages/DashboardPage'
import { DevicesPage } from './pages/DevicesPage'
import { LoginPage } from './pages/LoginPage'
import { MachinesPage } from './pages/MachinesPage'
import { NotFoundPage } from './pages/NotFoundPage'
import { ProductionEndPriorityPage } from './pages/ProductionEndPriorityPage'
import { RdsIntegrationPage } from './pages/RdsIntegrationPage'
import { ReportsPage } from './pages/ReportsPage'
import { UsersPage } from './pages/UsersPage'

function ProtectedLayout() {
  const { session } = useAuth()
  const location = useLocation()
  if (!session) return <Navigate to="/login" replace state={{ from: location.pathname }} />
  return <AppShell><Outlet /></AppShell>
}

export default function App() {
  const { session } = useAuth()
  return (
    <Routes>
      <Route path="/login" element={session ? <Navigate to="/" replace /> : <LoginPage />} />
      <Route element={<ProtectedLayout />}>
        <Route index element={<DashboardPage />} />
        <Route path="users" element={<UsersPage />} />
        <Route path="devices" element={<DevicesPage />} />
        <Route path="machines" element={<MachinesPage />} />
        <Route path="audit" element={<AuditPage />} />
        <Route path="reports" element={<ReportsPage />} />
        <Route path="production-end-priorities" element={<ProductionEndPriorityPage />} />
        <Route path="rds" element={<RdsIntegrationPage />} />
        <Route path="commissioning" element={<CommissioningReadinessPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  )
}
