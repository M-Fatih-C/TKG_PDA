import { useQuery, useQueryClient } from '@tanstack/react-query'
import { MonitorSmartphone, Pencil, Plus, Save, ShieldOff } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { Device, DeviceDraft } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, Check, EmptyState, ErrorState, Field, LoadingState, Modal, PageHeader, Panel, SearchInput, SelectInput, StatusPill, TableFrame, TextInput } from '../components/ui'

const blank: DeviceDraft = { code: '', name: '', type: 'PDA', isActive: true, metadata: {} }

export function DevicesPage() {
  const api = useAdminApi()
  const toast = useToast()
  const queryClient = useQueryClient()
  const query = useQuery({ queryKey: ['devices'], queryFn: ({ signal }) => api.devices(signal) })
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)
  const [editId, setEditId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const filtered = useMemo(() => (query.data ?? []).filter((device) => `${device.code} ${device.name} ${device.type}`.toLowerCase().includes(search.toLowerCase())), [query.data, search])
  const selected = query.data?.find((device) => device.id === editId) ?? null
  const execute = async (action: () => Promise<unknown>, message: string, close: () => void) => { setBusy(true); try { await action(); await queryClient.invalidateQueries({ queryKey: ['devices'] }); toast.success(message); close() } catch (error) { toast.error(error); await query.refetch() } finally { setBusy(false) } }

  if (query.isLoading) return <LoadingState label="Cihaz envanteri yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />
  return <div className="page"><PageHeader eyebrow="İstemci sağlama" title="Cihazlar" description="Saha PDA’larını ve gelecekteki servis istemcilerini sağlayın; cihaz kodları kimliktir, sır değildir." actions={<Button onClick={() => setCreating(true)}><Plus size={17} /> Cihaz ekle</Button>} />
    <div className="info-banner"><MonitorSmartphone size={19} /><div><strong>Admin Web cihazdan bağımsızdır.</strong><span>Yalnız `PDA` tipindeki aktif kayıtlar saha login’inde X-Device-Code ile kullanılabilir.</span></div></div>
    <Panel><div className="table-toolbar"><SearchInput value={search} onChange={setSearch} placeholder="Cihaz kodu, ad veya tip ara…" /><span>{filtered.length} kayıt</span></div>{filtered.length ? <TableFrame><thead><tr><th>Cihaz Kodu &amp; Ad</th><th>Tip</th><th>Saha İzni</th><th>Metadata</th><th>Durum</th><th className="align-right">İşlem</th></tr></thead><tbody>{filtered.map((device) => <tr key={device.id}><td><code className="mono-code">{device.code}</code><small className="table-subtitle">{device.name}</small></td><td><StatusPill value={device.type} tone={device.type === 'PDA' ? 'info' : 'neutral'} /></td><td>{device.type === 'PDA' && device.isActive ? <span className="saha-chip">Saha Girişi Yetkili</span> : <span className="muted">—</span>}</td><td><code className="json-chip">{Object.keys(device.metadata).length} alan</code></td><td><StatusPill value={device.isActive ? 'ACTIVE' : 'INACTIVE'} /></td><td><div className="row-actions"><Button size="sm" variant="secondary" onClick={() => setEditId(device.id)}>{device.isActive ? <Pencil size={15} /> : <ShieldOff size={15} />} Düzenle</Button></div></td></tr>)}</tbody></TableFrame> : <EmptyState title="Cihaz bulunamadı" description="Arama ölçütünü değiştirin veya yeni cihaz sağlayın." />}</Panel>
    {creating && <DeviceDialog key="create-device" open title="Yeni cihaz" initial={blank} codeEditable busy={busy} onClose={() => setCreating(false)} onSave={(draft) => execute(() => api.createDevice(draft), 'Cihaz sağlandı', () => setCreating(false))} />}
    {selected && <DeviceDialog key={selected.id} open title={`${selected.code} düzenle`} initial={{ code: selected.code, name: selected.name, type: selected.type, isActive: selected.isActive, metadata: selected.metadata }} busy={busy} onClose={() => setEditId(null)} onSave={(draft) => execute(() => api.updateDevice(selected, { name: draft.name, type: draft.type, isActive: draft.isActive, metadata: draft.metadata }), 'Cihaz güncellendi', () => setEditId(null))} />}
  </div>
}

function DeviceDialog({ open, title, initial, codeEditable = false, busy, onClose, onSave }: { open: boolean; title: string; initial: DeviceDraft; codeEditable?: boolean; busy: boolean; onClose: () => void; onSave: (draft: DeviceDraft) => void }) {
  const [state, setState] = useState(() => ({ draft: initial, metadata: JSON.stringify(initial.metadata, null, 2), error: '' }))
  const submit = (event: FormEvent) => { event.preventDefault(); try { const metadata = JSON.parse(state.metadata) as Record<string, unknown>; if (!metadata || Array.isArray(metadata) || typeof metadata !== 'object') throw new Error(); onSave({ ...state.draft, metadata }) } catch { setState({ ...state, error: 'Metadata geçerli bir JSON object olmalıdır.' }) } }
  return <Modal open={open} onClose={onClose} title={title} description={codeEditable ? 'Cihaz kodu oluşturulduktan sonra değiştirilemez.' : `ETag kontrollü güncelleme · ${initial.type}`} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button form="device-form" type="submit" disabled={busy}><Save size={16} /> Kaydet</Button></>}><form id="device-form" className="form-grid" onSubmit={submit}><Field label="Cihaz kodu"><TextInput required disabled={!codeEditable} value={state.draft.code} onChange={(event) => setState({ ...state, draft: { ...state.draft, code: event.target.value } })} /></Field><Field label="Görünen ad"><TextInput required value={state.draft.name} onChange={(event) => setState({ ...state, draft: { ...state.draft, name: event.target.value } })} /></Field><Field label="Cihaz tipi"><SelectInput value={state.draft.type} onChange={(event) => setState({ ...state, draft: { ...state.draft, type: event.target.value as Device['type'] } })}><option>PDA</option><option>ADMIN_WEB</option><option>SERVICE</option><option>OTHER</option></SelectInput></Field><Field label="Erişim durumu"><Check label="Aktif cihaz" checked={state.draft.isActive} onChange={(isActive) => setState({ ...state, draft: { ...state.draft, isActive } })} /></Field><div className="form-span"><Field label="Metadata JSON" error={state.error}><textarea className="input code-input" rows={6} value={state.metadata} onChange={(event) => setState({ ...state, metadata: event.target.value, error: '' })} /></Field></div></form></Modal>
}
