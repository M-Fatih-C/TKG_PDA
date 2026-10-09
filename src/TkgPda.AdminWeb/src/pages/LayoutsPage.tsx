import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Archive, CheckCircle2, CopyPlus, Edit3, ExternalLink, Plus, Rocket, Save } from 'lucide-react'
import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'
import type { Layout, Machine, SlotPartRecord } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, EmptyState, ErrorState, Field, LoadingState, Modal, PageHeader, Panel, SearchInput, SelectInput, StatusPill, TableFrame, TextInput, formatDate } from '../components/ui'

type LifecycleAction = { kind: 'publish' | 'archive' | 'clone'; layout: Layout }

export function LayoutsPage() {
  const api = useAdminApi()
  const toast = useToast()
  const client = useQueryClient()
  const layouts = useQuery({ queryKey: ['layouts'], queryFn: ({ signal }) => api.layouts(undefined, signal) })
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const [machineId, setMachineId] = useState('')
  const [search, setSearch] = useState('')
  const [createOpen, setCreateOpen] = useState(false)
  const [rename, setRename] = useState<Layout | null>(null)
  const [lifecycle, setLifecycle] = useState<LifecycleAction | null>(null)
  const [busy, setBusy] = useState(false)
  const filtered = useMemo(() => (layouts.data ?? []).filter((layout) => (!machineId || layout.machineId === machineId) && `${layout.code} ${layout.name}`.toLowerCase().includes(search.toLowerCase())), [layouts.data, machineId, search])
  const refresh = async () => { await client.invalidateQueries({ queryKey: ['layouts'] }) }
  const execute = async (action: () => Promise<unknown>, message: string, close?: () => void) => { setBusy(true); try { const result = await action(); await refresh(); toast.success(message); close?.(); return result } catch (error) { toast.error(error); await refresh() } finally { setBusy(false) } }
  if (layouts.isLoading || machines.isLoading) return <LoadingState label="Layout sürümleri yükleniyor…" />
  const failure = layouts.error ?? machines.error
  if (failure) return <ErrorState error={failure} onRetry={() => { void layouts.refetch(); void machines.refetch() }} />

  const machineName = (id: string) => machines.data?.find((item) => item.id === id)?.code ?? '—'
  return <div className="page"><PageHeader eyebrow="Fiziksel lojistik modeli" title="Layout sürümleri" description="Müşterinin fiziksel yerleşimini draft, publish ve archive yaşam döngüsüyle yönetin." actions={<Button onClick={() => setCreateOpen(true)}><Plus size={17} /> Yeni draft</Button>} />
    <div className="warning-banner"><strong>Robot Haritası Değildir</strong><span>Bu alan yalnız normalize 0–1 ekran/fiziksel yerleşim referansıdır. Robot koordinatı, waypoint, route veya stitch üretmez.</span></div>
    <Panel><div className="table-toolbar table-toolbar--split"><div className="toolbar-group"><SearchInput value={search} onChange={setSearch} placeholder="Layout kodu veya adı ara…" /><SelectInput value={machineId} onChange={(event) => setMachineId(event.target.value)}><option value="">Tüm makineler</option>{machines.data?.map((machine) => <option value={machine.id} key={machine.id}>{machine.code}</option>)}</SelectInput></div><span>{filtered.length} sürüm</span></div>
      {filtered.length ? <TableFrame><thead><tr><th>Layout</th><th>Makine</th><th>Sürüm</th><th>Slot / Atama</th><th>Yayın</th><th>Durum</th><th className="align-right">İşlemler</th></tr></thead><tbody>{filtered.map((layout) => <tr key={layout.id}><td><strong>{layout.code}</strong><small className="table-subtitle">{layout.name}</small></td><td>{machineName(layout.machineId)}</td><td><span className="version-chip">v{layout.version}</span></td><td>{layout.slotCount} slot · {layout.assignedVariantCount} varyant</td><td>{formatDate(layout.publishedAt)}</td><td><StatusPill value={layout.status} /></td><td><div className="row-actions"><Link className="button button--ghost button--sm" to={`/layouts/${layout.id}/editor`}><Edit3 size={15} /> {layout.status === 'DRAFT' ? 'Editör' : 'Görüntüle'}</Link>{layout.status === 'DRAFT' && <><Button size="sm" variant="ghost" onClick={() => setRename(layout)}>Ad</Button><Button size="sm" variant="primary" onClick={() => setLifecycle({ kind: 'publish', layout })}><Rocket size={15} /> Yayınla</Button></>}{layout.status !== 'DRAFT' && layout.status !== 'ARCHIVED' && <Button size="sm" variant="ghost" onClick={() => setLifecycle({ kind: 'clone', layout })}><CopyPlus size={15} /> Yeni sürüm</Button>}{layout.status === 'PUBLISHED' && <Button size="sm" variant="danger" onClick={() => setLifecycle({ kind: 'archive', layout })}><Archive size={15} /></Button>}</div></td></tr>)}</tbody></TableFrame> : <EmptyState title="Layout bulunamadı" description="Filtreleri değiştirin veya yeni bir draft oluşturun." />}
    </Panel>
    {createOpen && <CreateLayoutDialog open machines={machines.data ?? []} busy={busy} onClose={() => setCreateOpen(false)} onSave={(draft) => execute(() => api.createLayout(draft), 'Draft layout oluşturuldu', () => setCreateOpen(false))} />}
    {rename && <RenameDialog layout={rename} busy={busy} onClose={() => setRename(null)} onSave={(name) => execute(() => api.updateLayout(rename, name), 'Layout adı güncellendi', () => setRename(null))} />}
    {lifecycle && <LifecycleDialog action={lifecycle} busy={busy} onClose={() => setLifecycle(null)} onConfirm={() => execute(() => lifecycle.kind === 'publish' ? api.publishLayout(lifecycle.layout) : lifecycle.kind === 'archive' ? api.archiveLayout(lifecycle.layout) : api.cloneLayout(lifecycle.layout), lifecycle.kind === 'publish' ? 'Layout yayınlandı' : lifecycle.kind === 'archive' ? 'Layout arşivlendi' : 'Yeni draft sürümü oluşturuldu', () => setLifecycle(null))} />}
  </div>
}

function CreateLayoutDialog({ open, machines, busy, onClose, onSave }: { open: boolean; machines: Machine[]; busy: boolean; onClose: () => void; onSave: (draft: Pick<Layout, 'machineId' | 'code' | 'name'>) => void }) {
  const [draft, setDraft] = useState({ machineId: machines[0]?.id ?? '', code: '', name: '' })
  const submit = (event: FormEvent) => { event.preventDefault(); onSave(draft) }
  return <Modal open={open} onClose={onClose} title="Yeni draft layout" description="Aynı makine ve kod için aynı anda yalnız bir draft bulunabilir." footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button form="layout-create" type="submit" disabled={busy || !draft.machineId}><Save size={16} /> Oluştur</Button></>}><form id="layout-create" className="form-grid" onSubmit={submit}><Field label="Makine"><SelectInput value={draft.machineId} onChange={(event) => setDraft({ ...draft, machineId: event.target.value })}>{machines.map((machine) => <option value={machine.id} key={machine.id}>{machine.code} · {machine.displayName}</option>)}</SelectInput></Field><Field label="Layout kodu"><TextInput required value={draft.code} onChange={(event) => setDraft({ ...draft, code: event.target.value })} /></Field><div className="form-span"><Field label="Layout adı"><TextInput required value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></Field></div></form></Modal>
}

function RenameDialog({ layout, busy, onClose, onSave }: { layout: Layout; busy: boolean; onClose: () => void; onSave: (name: string) => void }) {
  const [name, setName] = useState(layout.name)
  return <Modal open onClose={onClose} title="Draft adını değiştir" description={`${layout.code} · v${layout.version} · ETag ${layout.concurrencyToken}`} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button disabled={busy || !name} onClick={() => onSave(name)}><Save size={16} /> Kaydet</Button></>}><Field label="Layout adı"><TextInput value={name} onChange={(event) => setName(event.target.value)} /></Field></Modal>
}

function LifecycleDialog({ action, busy, onClose, onConfirm }: { action: LifecycleAction; busy: boolean; onClose: () => void; onConfirm: () => void }) {
  const api = useAdminApi()
  const isPublish = action.kind === 'publish'
  const slotsQuery = useQuery({
    queryKey: ['publish-slots', action.layout.id],
    queryFn: ({ signal }) => api.slots(action.layout.id, signal),
    enabled: isPublish,
  })
  const mappingsQuery = useQuery({
    queryKey: ['publish-mappings', action.layout.id],
    queryFn: ({ signal }) => api.rdsSlotMappings(action.layout.id, signal),
    enabled: isPublish,
  })

  const [slotPartsMap, setSlotPartsMap] = useState<Record<string, SlotPartRecord[]>>({})
  const [partsLoading, setPartsLoading] = useState(false)

  useEffect(() => {
    if (!isPublish || !slotsQuery.data) return
    const operational = slotsQuery.data.filter((s) => s.isEnabled && s.role !== 'MACHINE')
    let active = true
    setPartsLoading(true)
    Promise.all(
      operational.map(async (slot) => {
        try {
          const parts = await api.slotParts(slot.id)
          return [slot.id, parts] as const
        } catch {
          return [slot.id, [] as SlotPartRecord[]] as const
        }
      }),
    ).then((results) => {
      if (active) {
        const map: Record<string, SlotPartRecord[]> = {}
        for (const [slotId, parts] of results) map[slotId] = parts
        setSlotPartsMap(map)
        setPartsLoading(false)
      }
    })
    return () => {
      active = false
    }
  }, [isPublish, slotsQuery.data, api])

  const validation = useMemo(() => {
    if (!isPublish) return { criticalErrors: [], warnings: [], canPublish: true, isLoading: false }
    if (slotsQuery.isLoading || mappingsQuery.isLoading || partsLoading) {
      return { criticalErrors: [], warnings: [], canPublish: false, isLoading: true }
    }

    const slots = slotsQuery.data ?? []
    const mappings = mappingsQuery.data ?? []
    const critical: string[] = []
    const warnings: string[] = []

    const enabledSlots = slots.filter((s) => s.isEnabled)
    const operationalSlots = enabledSlots.filter((s) => s.role !== 'MACHINE')

    if (operationalSlots.length === 0) {
      critical.push('En az bir aktif operasyonel slot bulunmalıdır (MACHINE harici).')
    }

    // 1. Boundary check: all enabled slots must have complete geometry within 0..1
    const unplacedOrOutOfBounds = enabledSlots.filter(
      (s) =>
        s.x === null ||
        s.y === null ||
        s.width === null ||
        s.height === null ||
        s.x < 0 ||
        s.y < 0 ||
        s.width <= 0 ||
        s.height <= 0 ||
        s.x + s.width > 1.0001 ||
        s.y + s.height > 1.0001,
    )
    if (unplacedOrOutOfBounds.length > 0) {
      critical.push(
        `Geometrisi eksik veya ekran sınırları dışına taşan ${unplacedOrOutOfBounds.length} slot var: ${unplacedOrOutOfBounds.map((s) => s.code).join(', ')}.`,
      )
    }

    // 2. Overlap check: no overlapping placed enabled slots
    const placed = enabledSlots.filter((s) => s.x !== null && s.y !== null && s.width !== null && s.height !== null)
    const overlappingCodes = new Set<string>()
    for (let i = 0; i < placed.length; i++) {
      for (let j = i + 1; j < placed.length; j++) {
        const a = placed[i]
        const b = placed[j]
        if (a.x! < b.x! + b.width! && a.x! + a.width! > b.x! && a.y! < b.y! + b.height! && a.y! + a.height! > b.y!) {
          overlappingCodes.add(a.code)
          overlappingCodes.add(b.code)
        }
      }
    }
    if (overlappingCodes.size > 0) {
      critical.push(
        `Fiziksel olarak çakışan slot geometrisi tespit edildi: ${Array.from(overlappingCodes).join(', ')}. Yayın öncesi çakışmaları düzeltin.`,
      )
    }

    // 3. Operational routing check: every operational slot must have at least one part assigned or be EMPTY_STORAGE or marked general buffer
    const missingRouting = operationalSlots.filter((s) => {
      const isGeneralBuffer = s.role === 'EMPTY_STORAGE' || Boolean(s.metadata?.is_general_buffer)
      if (isGeneralBuffer) return false
      const parts = slotPartsMap[s.id] ?? []
      return parts.length === 0
    })
    if (missingRouting.length > 0) {
      critical.push(
        `Parça eşlemesi olmayan ${missingRouting.length} operasyonel slot var: ${missingRouting.map((s) => s.code).join(', ')}. Her operasyonel slotta en az bir parça veya genel buffer rolü gereklidir.`,
      )
    }

    // 4. Commissioning check: operational slots with parts should have an RDS WorkSite mapping
    const missingCommissioning = operationalSlots.filter((s) => {
      const parts = slotPartsMap[s.id] ?? []
      if (parts.length === 0) return false
      const mapping = mappings.find((m) => m.slotId === s.id)
      return !mapping?.rdsWorkSiteId || !mapping?.executionReady
    })
    if (missingCommissioning.length > 0) {
      warnings.push(
        `RDS WorkSite eşlemesi eksik veya hazır olmayan ${missingCommissioning.length} slot var: ${missingCommissioning.map((s) => s.code).join(', ')}. AGV bu slotlara otomatik görev yapamaz.`,
      )
    }

    return {
      criticalErrors: critical,
      warnings,
      canPublish: critical.length === 0,
      isLoading: false,
    }
  }, [isPublish, slotsQuery.isLoading, mappingsQuery.isLoading, partsLoading, slotsQuery.data, mappingsQuery.data, slotPartsMap])

  const config =
    action.kind === 'publish'
      ? {
          title: 'Layout sürümünü yayınla',
          text: 'Yapısal doğrulama ve preflight kontrolleri çalıştırılır; aynı kodun eski published sürümü varsa atamalar yeni sürüme taşınır.',
          button: 'Yayınla',
          icon: Rocket,
        }
      : action.kind === 'archive'
        ? {
            title: 'Layout sürümünü arşivle',
            text: 'Aktif runtime veya transport referansı varsa API bu işlemi reddedecektir.',
            button: 'Arşivle',
            icon: Archive,
          }
        : {
            title: 'Yeni draft sürümü oluştur',
            text: 'Slotlar ve parça yönlendirmeleri (master.layout_slot_parts) yeni kimliklerle kopyalanacak ve published kaynak değişmeden kalacaktır.',
            button: 'Sürümü oluştur',
            icon: CopyPlus,
          }
  const Icon = config.icon

  return (
    <Modal
      open
      onClose={onClose}
      title={config.title}
      description={`${action.layout.code} · v${action.layout.version}`}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Vazgeç
          </Button>
          <Button
            variant={action.kind === 'archive' ? 'danger' : 'primary'}
            disabled={busy || (isPublish && (!validation.canPublish || validation.isLoading))}
            onClick={onConfirm}
          >
            <Icon size={16} /> {config.button}
          </Button>
        </>
      }
    >
      <div className="confirm-content">
        <Icon />
        <p>{config.text}</p>

        {isPublish && (
          <div className="preflight-summary">
            {validation.isLoading ? (
              <LoadingState label="Preflight doğrulamaları çalıştırılıyor…" />
            ) : (
              <>
                {validation.criticalErrors.length > 0 && (
                  <div className="preflight-banner preflight-banner--error" role="alert">
                    <AlertTriangle size={18} />
                    <div>
                      <strong>Yayın Öncesi Kritik Hatalar ({validation.criticalErrors.length}):</strong>
                      <ul className="preflight-list">
                        {validation.criticalErrors.map((err, idx) => (
                          <li key={idx}>{err}</li>
                        ))}
                      </ul>
                    </div>
                  </div>
                )}

                {validation.warnings.length > 0 && (
                  <div className="preflight-banner preflight-banner--warning" role="alert">
                    <AlertTriangle size={18} />
                    <div>
                      <strong>Devreye Alma Uyarıları ({validation.warnings.length}):</strong>
                      <ul className="preflight-list">
                        {validation.warnings.map((w, idx) => (
                          <li key={idx}>{w}</li>
                        ))}
                      </ul>
                    </div>
                  </div>
                )}

                {validation.canPublish && validation.warnings.length === 0 && (
                  <div className="preflight-banner preflight-banner--success">
                    <CheckCircle2 size={18} />
                    <span>Tüm preflight doğrulamaları başarılı. Sürüm yayınlanmaya uygun.</span>
                  </div>
                )}
              </>
            )}
          </div>
        )}

        <div className="info-banner">
          <ExternalLink size={17} /> Runtime varyant seçimi bu işlemle otomatik değiştirilmez.
        </div>
      </div>
    </Modal>
  )
}
