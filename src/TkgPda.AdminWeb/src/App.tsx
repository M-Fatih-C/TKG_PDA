import { Navigate, Outlet, Route, Routes, useLocation } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import { AppShell } from './components/AppShell'
import { AuditPage } from './pages/AuditPage'
import { CatalogPage } from './pages/CatalogPage'
import { ContainersPage } from './pages/ContainersPage'
import { DashboardPage } from './pages/DashboardPage'
import { DataQualityPage } from './pages/DataQualityPage'
import { DevicesPage } from './pages/DevicesPage'
import { LayoutEditorPage } from './pages/LayoutEditorPage'
import { LayoutsPage } from './pages/LayoutsPage'
import { LoginPage } from './pages/LoginPage'
import { MachinesPage } from './pages/MachinesPage'
import { NotFoundPage } from './pages/NotFoundPage'
import { ProductionEndPriorityPage } from './pages/ProductionEndPriorityPage'
import { RdsIntegrationPage } from './pages/RdsIntegrationPage'
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
        <Route path="layouts" element={<LayoutsPage />} />
        <Route path="layouts/:layoutId/editor" element={<LayoutEditorPage />} />
        <Route path="catalog" element={<CatalogPage />} />
        <Route path="containers" element={<ContainersPage />} />
        <Route path="data-quality" element={<DataQualityPage />} />
        <Route path="audit" element={<AuditPage />} />
        <Route path="production-end-priorities" element={<ProductionEndPriorityPage />} />
        <Route path="rds" element={<RdsIntegrationPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  )
}
