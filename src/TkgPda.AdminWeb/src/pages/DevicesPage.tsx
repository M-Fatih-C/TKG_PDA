import { useQuery, useQueryClient } from '@tanstack/react-query'
import { MonitorSmartphone, Pencil, Plus, Save, ShieldOff } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { Device, DeviceDraft, Machine } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, Check, EmptyState, ErrorState, Field, LoadingState, Modal, PageHeader, Panel, SearchInput, SelectInput, StatusPill, TableFrame, TextInput } from '../components/ui'
import { deviceTypeLabel } from '../displayLabels'

const blank: DeviceDraft = { code: '', name: '', type: 'PDA', isActive: true, metadata: {}, machineId: null }

export function DevicesPage() {
  const api = useAdminApi()
  const toast = useToast()
  const queryClient = useQueryClient()
  const query = useQuery({ queryKey: ['devices'], queryFn: ({ signal }) => api.devices(signal) })
  const machinesQuery = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)
  const [editId, setEditId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const machines = useMemo(() => machinesQuery.data ?? [], [machinesQuery.data])
  const machineMap = useMemo(() => new Map(machines.map((m) => [m.id, m])), [machines])

  const filtered = useMemo(() => (query.data ?? []).filter((device) => `${device.code} ${device.name} ${device.type}`.toLowerCase().includes(search.toLowerCase())), [query.data, search])
  const selected = query.data?.find((device) => device.id === editId) ?? null
  const execute = async (action: () => Promise<unknown>, message: string, close: () => void) => { setBusy(true); try { await action(); await queryClient.invalidateQueries({ queryKey: ['devices'] }); toast.success(message); close() } catch (error) { toast.error(error); await query.refetch() } finally { setBusy(false) } }

  if (query.isLoading || machinesQuery.isLoading) return <LoadingState label="Cihaz envanteri yükleniyor…" />
  const failure = query.error ?? machinesQuery.error
  if (failure) return <ErrorState error={failure} onRetry={() => { void query.refetch(); void machinesQuery.refetch() }} />
  return <div className="page"><PageHeader eyebrow="Cihaz Yönetimi" title="Saha Cihazları & PDA'lar" description="Saha el terminalleri, tabletler ve operasyon cihazlarının yetkilendirmesini yönetin." actions={<Button onClick={() => setCreating(true)}><Plus size={17} /> Cihaz ekle</Button>} />
    <div className="info-banner"><MonitorSmartphone size={19} /><div><strong>Saha Cihaz Oturumu</strong><span>Yalnız aktif ve PDA tipindeki kayıtlar saha terminal oturumu açabilir.</span></div></div>
    <Panel><div className="table-toolbar"><SearchInput value={search} onChange={setSearch} placeholder="Cihaz kodu, ad veya tip ara…" /><span>{filtered.length} kayıt</span></div>{filtered.length ? <TableFrame><thead><tr><th>Cihaz Kodu &amp; Ad</th><th>Tip</th><th>Makine Bağlantısı</th><th>Saha İzni</th><th>Ek Bilgiler</th><th>Durum</th><th className="align-right">İşlem</th></tr></thead><tbody>{filtered.map((device) => {
      const boundMachine = device.machineId ? machineMap.get(device.machineId) : null
      return <tr key={device.id}>
        <td><code className="mono-code">{device.code}</code><small className="table-subtitle">{device.name}</small></td>
        <td><StatusPill value={device.type} tone={device.type === 'PDA' ? 'info' : 'neutral'} /></td>
        <td>
          {device.machineId ? (
            <div>
              <span className="mono-code">{boundMachine ? boundMachine.code : device.machineId}</span>
              {boundMachine?.displayName && <small className="table-subtitle">{boundMachine.displayName}</small>}
            </div>
          ) : (
            <span className="muted">Depo / Serbest Saha Cihazı</span>
          )}
        </td>
        <td>{device.type === 'PDA' && device.isActive ? <span className="saha-chip">Saha Girişi Yetkili</span> : <span className="muted">—</span>}</td>
        <td><code className="json-chip">{Object.keys(device.metadata).length} alan</code></td>
        <td><StatusPill value={device.isActive ? 'ACTIVE' : 'INACTIVE'} /></td>
        <td><div className="row-actions"><Button size="sm" variant="secondary" onClick={() => setEditId(device.id)}>{device.isActive ? <Pencil size={15} /> : <ShieldOff size={15} />} Düzenle</Button></div></td>
      </tr>
    })}</tbody></TableFrame> : <EmptyState title="Cihaz bulunamadı" description="Arama ölçütünü değiştirin veya yeni cihaz sağlayın." />}</Panel>
    {creating && <DeviceDialog key="create-device" open title="Yeni cihaz" initial={blank} machines={machines} codeEditable busy={busy} onClose={() => setCreating(false)} onSave={(draft) => execute(() => api.createDevice(draft), 'Cihaz sağlandı', () => setCreating(false))} />}
    {selected && <DeviceDialog key={selected.id} open title={`${selected.code} düzenle`} initial={{ code: selected.code, name: selected.name, type: selected.type, isActive: selected.isActive, metadata: selected.metadata, machineId: selected.machineId ?? null }} machines={machines} busy={busy} onClose={() => setEditId(null)} onSave={(draft) => execute(() => api.updateDevice(selected, { name: draft.name, type: draft.type, isActive: draft.isActive, metadata: draft.metadata, machineId: draft.machineId ?? null }), 'Cihaz güncellendi', () => setEditId(null))} />}
  </div>
}

function DeviceDialog({ open, title, initial, machines, codeEditable = false, busy, onClose, onSave }: { open: boolean; title: string; initial: DeviceDraft; machines: Machine[]; codeEditable?: boolean; busy: boolean; onClose: () => void; onSave: (draft: DeviceDraft) => void }) {
  const [state, setState] = useState(() => ({ draft: initial, metadata: JSON.stringify(initial.metadata, null, 2), error: '' }))
  const submit = (event: FormEvent) => {
    event.preventDefault()
    try {
      const metadata = JSON.parse(state.metadata) as Record<string, unknown>
      if (!metadata || Array.isArray(metadata) || typeof metadata !== 'object') throw new Error()
      onSave({ ...state.draft, metadata, machineId: state.draft.type === 'PDA' ? state.draft.machineId : null })
    } catch {
      setState({ ...state, error: 'Ek bilgiler geçerli bir JSON nesnesi olmalıdır.' })
    }
  }
  return <Modal open={open} onClose={onClose} title={title} description={codeEditable ? 'Cihaz kodu oluşturulduktan sonra değiştirilemez.' : `Güvenli eşzamanlı güncelleme · ${deviceTypeLabel(initial.type)}`} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button form="device-form" type="submit" disabled={busy}><Save size={16} /> Kaydet</Button></>}><form id="device-form" className="form-grid" onSubmit={submit}><Field label="Cihaz kodu"><TextInput required disabled={!codeEditable} value={state.draft.code} onChange={(event) => setState({ ...state, draft: { ...state.draft, code: event.target.value } })} /></Field><Field label="Görünen ad"><TextInput required value={state.draft.name} onChange={(event) => setState({ ...state, draft: { ...state.draft, name: event.target.value } })} /></Field><Field label="Cihaz tipi"><SelectInput value={state.draft.type} onChange={(event) => setState({ ...state, draft: { ...state.draft, type: event.target.value as Device['type'], machineId: event.target.value !== 'PDA' ? null : state.draft.machineId } })}><option value="PDA">PDA</option><option value="ADMIN_WEB">Yönetim Paneli</option><option value="SERVICE">Servis</option><option value="OTHER">Diğer</option></SelectInput></Field><Field label="Makine Bağlantısı"><SelectInput value={state.draft.machineId ?? ''} onChange={(event) => setState({ ...state, draft: { ...state.draft, machineId: event.target.value ? event.target.value : null } })}><option value="">Bağlı Değil / Depo Tableti</option>{machines.map((m) => <option key={m.id} value={m.id}>{m.code}{m.displayName ? ` — ${m.displayName}` : ''}</option>)}</SelectInput></Field><Field label="Erişim durumu"><Check label="Aktif cihaz" checked={state.draft.isActive} onChange={(isActive) => setState({ ...state, draft: { ...state.draft, isActive } })} /></Field><div className="form-span"><Field label="Ek bilgiler (JSON)" error={state.error}><textarea className="input code-input" rows={6} value={state.metadata} onChange={(event) => setState({ ...state, metadata: event.target.value, error: '' })} /></Field></div></form></Modal>
}
