import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import '@fontsource-variable/inter'
import '@fontsource-variable/manrope'
import App from './App'
import { ApiProvider } from './api/ApiContext'
import { AuthProvider } from './auth/AuthContext'
import { ToastProvider } from './components/Toast'
import './index.css'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 20_000, retry: 1, refetchOnWindowFocus: false },
    mutations: { retry: false },
  },
})

const baseName = (import.meta.env.BASE_URL ?? '/admin/').replace(/\/$/, '') || '/admin'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter basename={baseName}>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <ApiProvider>
            <ToastProvider><App /></ToastProvider>
          </ApiProvider>
        </AuthProvider>
      </QueryClientProvider>
    </BrowserRouter>
  </StrictMode>,
)
