import { AlertTriangle, CircleHelp, Inbox, LoaderCircle, RefreshCw, Search, X } from 'lucide-react'
import type { ButtonHTMLAttributes, InputHTMLAttributes, PropsWithChildren, ReactNode, SelectHTMLAttributes } from 'react'
import { ApiProblem } from '../api/client'
import { displayStatusLabel } from '../displayLabels'

export function Button({ className = '', variant = 'primary', size = 'md', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary' | 'ghost' | 'danger'; size?: 'sm' | 'md' }) {
  return <button className={`button button--${variant} button--${size} ${className}`} {...props} />
}

export function IconButton({ label, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return <button className="icon-button" aria-label={label} title={label} {...props} />
}

export function PageHeader({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description?: string; actions?: ReactNode }) {
  return (
    <header className="page-header">
      <div>{eyebrow && <span className="eyebrow">{eyebrow}</span>}<h1>{title}</h1>{description && <p>{description}</p>}</div>
      {actions && <div className="page-header__actions">{actions}</div>}
    </header>
  )
}

export function Panel({ children, className = '' }: PropsWithChildren<{ className?: string }>) {
  return <section className={`panel ${className}`}>{children}</section>
}

export function PanelHeader({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return <div className="panel-header"><div><h2>{title}</h2>{description && <p>{description}</p>}</div>{action}</div>
}

export function InfoBox({ title, children, tone = 'info', className = '' }: PropsWithChildren<{ title: string; tone?: 'info' | 'warning'; className?: string }>) {
  return <aside className={`info-box info-box--${tone} ${className}`} aria-label={`${title} bilgisi`}><CircleHelp size={20} /><div><strong>{title}</strong><span>{children}</span></div></aside>
}

export function StatusPill({ value, tone }: { value: string; tone?: 'success' | 'warning' | 'danger' | 'neutral' | 'info' }) {
  const normalized = tone ?? (['ACTIVE', 'PUBLISHED', 'LOW'].includes(value) ? 'success' : ['DRAFT', 'MEDIUM'].includes(value) ? 'warning' : ['ARCHIVED', 'HIGH', 'INACTIVE'].includes(value) ? 'danger' : 'neutral')
  const label = displayStatusLabel(value)
  return <span className={`status status--${normalized}`} title={label === value ? undefined : `Teknik kod: ${value}`}>{label}</span>
}

export function Field({ label, hint, error, children }: PropsWithChildren<{ label: string; hint?: string; error?: string }>) {
  return <label className="field"><span>{label}</span>{children}{hint && <small>{hint}</small>}{error && <small className="field__error">{error}</small>}</label>
}

export function TextInput(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input className="input" {...props} />
}

export function SelectInput(props: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className="input" {...props} />
}

export function SearchInput({ value, onChange, placeholder = 'Ara…' }: { value: string; onChange: (value: string) => void; placeholder?: string }) {
  return <label className="search-input"><Search size={17} /><input value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} /></label>
}

export function Check({ label, checked, onChange, disabled }: { label: string; checked: boolean; onChange: (checked: boolean) => void; disabled?: boolean }) {
  return <label className="check"><input type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} /><span>{label}</span></label>
}

export function Modal({ title, description, open, onClose, children, footer, wide = false }: PropsWithChildren<{ title: string; description?: string; open: boolean; onClose: () => void; footer?: ReactNode; wide?: boolean }>) {
  if (!open) return null
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <section className={`modal ${wide ? 'modal--wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <header><div><h2>{title}</h2>{description && <p>{description}</p>}</div><IconButton label="Pencereyi kapat" onClick={onClose}><X size={19} /></IconButton></header>
        <div className="modal__body">{children}</div>
        {footer && <footer>{footer}</footer>}
      </section>
    </div>
  )
}

export function LoadingState({ label = 'Veriler yükleniyor…' }: { label?: string }) {
  return <div className="state"><LoaderCircle className="spin" /><strong>{label}</strong></div>
}

export function EmptyState({ title, description, action }: { title: string; description: string; action?: ReactNode }) {
  return <div className="state"><Inbox /><strong>{title}</strong><span>{description}</span>{action}</div>
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const problem = error instanceof ApiProblem ? error : null
  const databaseUnavailable = problem?.code === 'DATABASE_UNAVAILABLE'
  return (
    <div className={`error-state ${problem?.isConcurrencyConflict ? 'error-state--conflict' : ''}`} role="alert">
      <AlertTriangle />
      <div>
        <strong>{problem?.isConcurrencyConflict ? 'Güncel sürüm gerekli' : databaseUnavailable ? 'Sunucu hazırlanıyor' : 'Veri alınamadı'}</strong>
        <p>{databaseUnavailable ? 'Veritabanı bağlantısı henüz hazır değil. Birkaç saniye sonra Yenile ile tekrar deneyin.' : error instanceof Error ? error.message : 'Beklenmeyen bir hata oluştu.'}</p>
        {problem && <small>{problem.code}{problem.correlationId ? ` · ${problem.correlationId}` : ''}</small>}
      </div>
      {onRetry && <Button variant="secondary" size="sm" onClick={onRetry}><RefreshCw size={15} /> Yenile</Button>}
    </div>
  )
}

export function TableFrame({ children }: PropsWithChildren) {
  return <div className="table-frame"><table>{children}</table></div>
}

export function Definition({ label, value }: { label: string; value: ReactNode }) {
  return <div className="definition"><span>{label}</span><strong>{value}</strong></div>
}

export const formatDate = (value?: string | null) => value
  ? new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
  : '—'

export const compactNumber = (value: number) => new Intl.NumberFormat('tr-TR', { notation: value > 999 ? 'compact' : 'standard' }).format(value)
