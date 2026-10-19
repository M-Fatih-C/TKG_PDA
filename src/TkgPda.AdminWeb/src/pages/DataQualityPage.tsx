import { useQuery } from '@tanstack/react-query'
import { AlertOctagon, CircleAlert, Filter, Info } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useAdminApi } from '../api/ApiContext'
import { EmptyState, ErrorState, LoadingState, PageHeader, Panel, SearchInput, SelectInput, StatusPill, TableFrame } from '../components/ui'

export function DataQualityPage() {
  const api = useAdminApi()
  const query = useQuery({ queryKey: ['data-issues'], queryFn: ({ signal }) => api.dataIssues(signal) })
  const [search, setSearch] = useState(''); const [severity, setSeverity] = useState(''); const [kind, setKind] = useState('')
  const kinds = useMemo(() => [...new Set((query.data ?? []).map((item) => item.kind))].sort(), [query.data])
  const filtered = useMemo(() => (query.data ?? []).filter((issue) => (!severity || issue.severity === severity) && (!kind || issue.kind === kind) && `${issue.code} ${issue.message} ${issue.entityType}`.toLowerCase().includes(search.toLowerCase())), [kind, query.data, search, severity])
  if (query.isLoading) return <LoadingState label="Veri kalite kanıtları yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />
  const count = (value: string) => query.data?.filter((item) => item.severity === value).length ?? 0
  return <div className="page"><PageHeader eyebrow="Veri Doğrulama" title="Veri Kalitesi" description="Veri tutarlılığı, eşleşme eksiklikleri ve sistem doğrulama bildirimleri." />
    <div className="quality-summary"><div className="quality-card quality-card--high"><AlertOctagon /><span><strong>{count('HIGH')}</strong><small>Yüksek öncelik</small></span></div><div className="quality-card quality-card--medium"><CircleAlert /><span><strong>{count('MEDIUM')}</strong><small>Orta öncelik</small></span></div><div className="quality-card quality-card--low"><Info /><span><strong>{count('LOW')}</strong><small>Düşük öncelik</small></span></div></div>
    <div className="info-banner"><Info size={19} /><div><strong>Otomatik Doğrulama Kayıtları</strong><span>Sistem tarafından tespit edilen veri doğrulama ve yapılandırma uyarıları listelenmektedir.</span></div></div>
    <Panel><div className="table-toolbar table-toolbar--split"><div className="toolbar-group"><SearchInput value={search} onChange={setSearch} placeholder="Kod, mesaj veya entity ara…" /><SelectInput value={severity} onChange={(event) => setSeverity(event.target.value)}><option value="">Tüm öncelikler</option><option>HIGH</option><option>MEDIUM</option><option>LOW</option></SelectInput><SelectInput value={kind} onChange={(event) => setKind(event.target.value)}><option value="">Tüm konu türleri</option>{kinds.map((value) => <option key={value}>{value}</option>)}</SelectInput></div><span><Filter size={15} /> {filtered.length} konu</span></div>{filtered.length ? <TableFrame><thead><tr><th>Öncelik</th><th>Konu</th><th>Entity</th><th>Kanıt kodu</th><th>Açıklama</th></tr></thead><tbody>{filtered.map((issue) => <tr key={`${issue.kind}-${issue.entityId}-${issue.code}`}><td><StatusPill value={issue.severity} /></td><td><strong>{issue.kind.replaceAll('_', ' ')}</strong></td><td>{issue.entityType}<small className="table-subtitle mono">{issue.entityId}</small></td><td><code>{issue.code}</code></td><td className="message-cell">{issue.message}</td></tr>)}</tbody></TableFrame> : <EmptyState title="Filtreyle eşleşen konu yok" description="Filtreleri temizleyin veya başka bir arama deneyin." />}</Panel>
  </div>
}
