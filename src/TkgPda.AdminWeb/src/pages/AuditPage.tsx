import { useQuery } from '@tanstack/react-query'
import { Braces, ChevronDown, ChevronRight, Fingerprint } from 'lucide-react'
import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'
import { EmptyState, ErrorState, Field, LoadingState, PageHeader, Panel, SelectInput, StatusPill, TableFrame, TextInput, formatDate } from '../components/ui'

export function AuditPage() {
  const api = useAdminApi()
  const [params] = useSearchParams()
  const [entityType, setEntityType] = useState(params.get('entityType') ?? '')
  const [action, setAction] = useState(params.get('action') ?? '')
  const [actor, setActor] = useState(params.get('actor') ?? '')
  const [from, setFrom] = useState(params.get('from') ?? '')
  const [to, setTo] = useState(params.get('to') ?? '')
  const correlationId = params.get('correlationId') ?? ''
  const entityId = params.get('entityId') ?? ''
  const [expanded, setExpanded] = useState<number | null>(null)
  const filters = { limit: 200, entityType: entityType || undefined, action: action || undefined, actor: actor || undefined, from: from || undefined, to: to || undefined, correlationId: correlationId || undefined, entityId: entityId || undefined }
  const query = useQuery({ queryKey: ['audit', filters], queryFn: ({ signal }) => api.audit(filters, signal) })
  const rows = query.data ?? []
  if (query.isLoading) return <LoadingState label="Audit geçmişi yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />
  return <div className="page"><PageHeader eyebrow="Teknik Audit" title="Teknik Audit Geçmişi" description="Aksiyon, entity, correlation ve before/after JSON. Operasyonel okuma için Operasyon Kayıtları & Raporlar sayfasını kullanın." />
    <Panel><div className="table-toolbar table-toolbar--split"><div className="toolbar-group"><Field label="Kullanıcı"><TextInput aria-label="Audit kullanıcısı" value={actor} onChange={(event) => setActor(event.target.value)} /></Field><Field label="Aksiyon"><TextInput aria-label="Audit aksiyonu" value={action} onChange={(event) => setAction(event.target.value)} /></Field><SelectInput aria-label="Entity türü" value={entityType} onChange={(event) => setEntityType(event.target.value)}><option value="">Tüm entity türleri</option><option>TRANSPORT_REQUEST</option><option>AUTH_SESSION</option><option>AUTH_USER</option></SelectInput><TextInput aria-label="Audit başlangıç" type="datetime-local" value={from} onChange={(event) => setFrom(event.target.value)} /><TextInput aria-label="Audit bitiş" type="datetime-local" value={to} onChange={(event) => setTo(event.target.value)} /></div><span>{rows.length} kayıt</span></div>{rows.length ? <TableFrame><thead><tr><th aria-label="Detay" /><th>Zaman</th><th>Aktör</th><th>Aksiyon</th><th>Entity</th><th>Correlation</th></tr></thead><tbody>{rows.map((item) => <AuditRows key={item.id} item={item} open={expanded === item.id} onToggle={() => setExpanded(expanded === item.id ? null : item.id)} />)}</tbody></TableFrame> : <EmptyState title="Audit kaydı bulunamadı" description="Filtreleri değiştirin veya ilk yapılandırma hareketini bekleyin." />}</Panel>
  </div>
}

function AuditRows({ item, open, onToggle }: { item: Awaited<ReturnType<ReturnType<typeof useAdminApi>['audit']>>[number]; open: boolean; onToggle: () => void }) {
  return <><tr className="clickable-row" onClick={onToggle}><td>{open ? <ChevronDown size={17} /> : <ChevronRight size={17} />}</td><td>{formatDate(item.createdAt)}</td><td><strong>{item.actorUsername ?? 'Sistem'}</strong><small className="table-subtitle">{item.ipAddress ?? 'IP yok'}</small></td><td><StatusPill value={item.action.replace('ADMIN_', '')} tone="info" /></td><td>{item.entityType}<small className="table-subtitle mono">{item.entityId ?? '—'}</small></td><td><span className="correlation"><Fingerprint size={15} /> {item.correlationId?.slice(0, 8) ?? '—'}</span></td></tr>{open && <tr className="audit-detail"><td colSpan={6}><div><section><h4><Braces size={16} /> Önce</h4><pre>{JSON.stringify(item.before, null, 2) || 'null'}</pre></section><section><h4><Braces size={16} /> Sonra</h4><pre>{JSON.stringify(item.after, null, 2) || 'null'}</pre></section></div></td></tr>}</>
}
