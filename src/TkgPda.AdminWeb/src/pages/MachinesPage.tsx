import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Factory, Pencil, Plus, Save, Split } from 'lucide-react'
import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { Machine, Variant } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, Check, EmptyState, ErrorState, Field, InfoBox, LoadingState, Modal, PageHeader, Panel, PanelHeader, SelectInput, StatusPill, TableFrame, TextInput } from '../components/ui'

export function MachinesPage() {
  const api = useAdminApi()
  const toast = useToast()
  const client = useQueryClient()
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const variants = useQuery({ queryKey: ['variants'], queryFn: ({ signal }) => api.variants(undefined, signal) })
  const layouts = useQuery({ queryKey: ['layouts'], queryFn: ({ signal }) => api.layouts(undefined, signal) })
  const [machineId, setMachineId] = useState('')
  const [editingMachine, setEditingMachine] = useState<Machine | null>(null)
  const [editingVariant, setEditingVariant] = useState<Variant | null>(null)
  const [creatingVariant, setCreatingVariant] = useState(false)
  const [busy, setBusy] = useState(false)
  useEffect(() => { if (!machineId && machines.data?.[0]) setMachineId(machines.data[0].id) }, [machineId, machines.data])
  const machine = machines.data?.find((item) => item.id === machineId)
  const machineVariants = useMemo(() => (variants.data ?? []).filter((item) => item.machineId === machineId).sort((a, b) => a.displayOrder - b.displayOrder), [machineId, variants.data])
  const publishedLayouts = (layouts.data ?? []).filter((item) => item.machineId === machineId && item.status === 'PUBLISHED')
  const refresh = async () => { await Promise.all([client.invalidateQueries({ queryKey: ['machines'] }), client.invalidateQueries({ queryKey: ['variants'] }), client.invalidateQueries({ queryKey: ['layouts'] })]) }
  const execute = async (action: () => Promise<unknown>, message: string, close?: () => void) => { setBusy(true); try { await action(); await refresh(); toast.success(message); close?.() } catch (error) { toast.error(error); await refresh() } finally { setBusy(false) } }
  const all = [machines, variants, layouts]
  if (all.some((query) => query.isLoading)) return <LoadingState label="Makine ve varyant konfigürasyonu yükleniyor…" />
  const failure = all.find((query) => query.error)?.error
  if (failure) return <ErrorState error={failure} onRetry={() => all.forEach((query) => void query.refetch())} />

  return <div className="page"><PageHeader eyebrow="Üretim Bağlamı" title="Makine & Varyantlar" description="Operasyonel üretim makinelerini ve her makinenin üretim varyantlarını yönetin." />
    <InfoBox title="Makine ve varyant ilişkisi">Her varyant yalnızca bağlı olduğu makinede kullanılır. Varsayılan yerleşim planını değiştirmek, operatörün üretim bağlamında kullanacağı onaylı fiziksel alanları belirler.</InfoBox>
    <div className="machine-tabs" role="tablist">{machines.data?.map((item) => <button role="tab" aria-selected={item.id === machineId} className={item.id === machineId ? 'active' : ''} key={item.id} onClick={() => setMachineId(item.id)}><strong>{item.code}</strong><small>{item.displayName}</small></button>)}</div>
    {machine && <div className="machine-summary"><div className="machine-summary__icon"><Factory /></div><div><span className="eyebrow">Seçili makine</span><h2>{machine.code} · {machine.displayName}</h2><p>{machineVariants.length} varyant · {publishedLayouts.length} aktif yerleşim planı</p></div><StatusPill value={machine.isActive ? 'ACTIVE' : 'INACTIVE'} /><Button variant="secondary" onClick={() => setEditingMachine(machine)}><Pencil size={16} /> Makine bilgisi</Button></div>}
    <Panel><PanelHeader title="Üretim varyantları" description="Üretim varyantları ve varsayılan yerleşim planı eşleşmeleri." action={<Button onClick={() => setCreatingVariant(true)} disabled={!machine}><Plus size={16} /> Varyant ekle</Button>} />{machineVariants.length ? <TableFrame><thead><tr><th>Varyant</th><th>Görünen ad</th><th>Varsayılan yerleşim planı</th><th>Sıra</th><th>Durum</th><th className="align-right">İşlem</th></tr></thead><tbody>{machineVariants.map((variant) => <tr key={variant.id}><td><strong>{variant.code}</strong></td><td>{variant.name}</td><td><SelectInput aria-label={`${variant.code} varsayılan yerleşim planı`} value={variant.defaultLayoutId ?? ''} disabled={busy || !variant.isActive} onChange={(event) => execute(() => api.setDefaultLayout(variant, event.target.value), 'Varsayılan yerleşim planı güncellendi')}><option value="" disabled>Atanmamış</option>{publishedLayouts.map((layout) => <option value={layout.id} key={layout.id}>{layout.code} · v{layout.version}</option>)}</SelectInput></td><td>{variant.displayOrder}</td><td><StatusPill value={variant.isActive ? 'ACTIVE' : 'INACTIVE'} /></td><td><div className="row-actions"><Button size="sm" variant="secondary" onClick={() => setEditingVariant(variant)}><Pencil size={15} /> Düzenle</Button></div></td></tr>)}</tbody></TableFrame> : <EmptyState title="Varyant bulunamadı" description="Bu makine için henüz üretim varyantı tanımlanmamış." />}</Panel>
    {machine && creatingVariant && <VariantDialog key={`new-${machine.id}`} open machine={machine} busy={busy} onClose={() => setCreatingVariant(false)} onSave={(draft) => execute(() => api.createVariant({ machineId: draft.machineId, code: draft.code, name: draft.name, displayOrder: draft.displayOrder }), 'Varyant oluşturuldu', () => setCreatingVariant(false))} />}
    {editingVariant && <VariantDialog key={editingVariant.id} open machine={machine!} variant={editingVariant} busy={busy} onClose={() => setEditingVariant(null)} onSave={(draft) => execute(() => api.updateVariant(editingVariant, { name: draft.name, displayOrder: draft.displayOrder, isActive: draft.isActive }), 'Varyant güncellendi', () => setEditingVariant(null))} />}
    {editingMachine && <MachineDialog machine={editingMachine} busy={busy} onClose={() => setEditingMachine(null)} onSave={(draft) => execute(() => api.updateMachine(editingMachine, draft), 'Makine bilgisi güncellendi', () => setEditingMachine(null))} />}
  </div>
}

function MachineDialog({ machine, busy, onClose, onSave }: { machine: Machine; busy: boolean; onClose: () => void; onSave: (draft: Pick<Machine, 'displayName' | 'displayOrder' | 'isActive'>) => void }) {
  const [draft, setDraft] = useState({ displayName: machine.displayName, displayOrder: machine.displayOrder, isActive: machine.isActive })
  return <Modal open onClose={onClose} title={`${machine.code} makine bilgisi`} description="Makine sistem kodu benzersizdir ve değiştirilemez." footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button disabled={busy} onClick={() => onSave(draft)}><Save size={16} /> Kaydet</Button></>}><div className="form-grid"><Field label="Görünen ad"><TextInput value={draft.displayName} onChange={(event) => setDraft({ ...draft, displayName: event.target.value })} /></Field><Field label="Görünüm sırası"><TextInput type="number" min={0} value={draft.displayOrder} onChange={(event) => setDraft({ ...draft, displayOrder: Number(event.target.value) })} /></Field><div className="form-span"><Check label="Aktif makine" checked={draft.isActive} onChange={(isActive) => setDraft({ ...draft, isActive })} /></div></div></Modal>
}

function VariantDialog({ open, machine, variant, busy, onClose, onSave }: { open: boolean; machine: Machine; variant?: Variant; busy: boolean; onClose: () => void; onSave: (draft: Pick<Variant, 'machineId' | 'code' | 'name' | 'displayOrder' | 'isActive'>) => void }) {
  const [draft, setDraft] = useState({ machineId: machine.id, code: variant?.code ?? '', name: variant?.name ?? '', displayOrder: variant?.displayOrder ?? 0, isActive: variant?.isActive ?? true })
  const submit = (event: FormEvent) => { event.preventDefault(); onSave(draft) }
  return <Modal open={open} onClose={onClose} title={variant ? `${variant.code} düzenle` : `${machine.code} için varyant`} description={variant ? 'Varyant sistem kodu benzersizdir ve değiştirilemez.' : 'Yeni varyant makinenin mevcut yerleşim planını kullanabilir.'} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button form="variant-form" type="submit" disabled={busy}><Split size={16} /> {variant ? 'Kaydet' : 'Oluştur'}</Button></>}><form id="variant-form" className="form-grid" onSubmit={submit}><Field label="Varyant kodu"><TextInput required disabled={!!variant} value={draft.code} onChange={(event) => setDraft({ ...draft, code: event.target.value })} /></Field><Field label="Görünen ad"><TextInput required value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></Field><Field label="Görünüm sırası"><TextInput type="number" min={0} value={draft.displayOrder} onChange={(event) => setDraft({ ...draft, displayOrder: Number(event.target.value) })} /></Field><Field label="Durum"><Check label="Aktif varyant" checked={draft.isActive} onChange={(isActive) => setDraft({ ...draft, isActive })} /></Field></form></Modal>
}
