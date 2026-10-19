import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowLeft, BoxSelect, Grid3X3, LockKeyhole, Minus, MousePointer2, Plus, Save, Trash2, ZoomIn, ZoomOut } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type FormEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAdminApi } from '../api/ApiContext'
import { ApiProblem } from '../api/client'
import type { Part, RdsNavigationProfile, RdsSlotMapping, RdsWorkSite, Slot, SlotDraft, SlotPartRecord, SlotRole } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, Check, Definition, EmptyState, ErrorState, Field, LoadingState, Modal, Panel, SelectInput, StatusPill, TextInput } from '../components/ui'

const roles: SlotRole[] = ['MACHINE', 'STAGING', 'EMPTY_STORAGE', 'FULL_STORAGE', 'WAREHOUSE', 'PICKUP', 'DROP', 'SHARED', 'OTHER']
const snapValue = (value: number, grid: number) => Math.round(value / grid) * grid
const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))
const placed = (slot: Pick<SlotDraft, 'x' | 'y' | 'width' | 'height'>) => slot.x !== null && slot.y !== null && slot.width !== null && slot.height !== null
const toDraft = (slot: Slot): SlotDraft => ({
  code: slot.code,
  displayName: slot.displayName,
  role: slot.role,
  defaultContainerTypeId: slot.defaultContainerTypeId,
  x: slot.x,
  y: slot.y,
  width: slot.width,
  height: slot.height,
  displayOrder: slot.displayOrder,
  allowsEmptyLoad: slot.allowsEmptyLoad,
  allowsFullLoad: slot.allowsFullLoad,
  isEnabled: slot.isEnabled,
  metadata: slot.metadata,
})

interface DragState {
  mode: 'move' | 'resize'
  startX: number
  startY: number
  bounds: DOMRect
  base: SlotDraft
}

export function LayoutEditorPage() {
  const { layoutId = '' } = useParams()
  const api = useAdminApi()
  const toast = useToast()
  const client = useQueryClient()
  const layouts = useQuery({ queryKey: ['layouts'], queryFn: ({ signal }) => api.layouts(undefined, signal) })
  const slots = useQuery({ queryKey: ['slots', layoutId], queryFn: ({ signal }) => api.slots(layoutId, signal), enabled: !!layoutId })
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const containers = useQuery({ queryKey: ['containers'], queryFn: ({ signal }) => api.containers(signal) })
  const parts = useQuery({ queryKey: ['parts'], queryFn: ({ signal }) => api.parts(signal) })
  const rdsProfiles = useQuery({ queryKey: ['rds-navigation-profiles'], queryFn: ({ signal }) => api.rdsNavigationProfiles(signal) })
  const rdsMappings = useQuery({ queryKey: ['rds-mappings', layoutId], queryFn: ({ signal }) => api.rdsSlotMappings(layoutId, signal), enabled: !!layoutId })
  const layout = layouts.data?.find((item) => item.id === layoutId)
  const machine = machines.data?.find((item) => item.id === layout?.machineId)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [working, setWorking] = useState<SlotDraft | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [grid, setGrid] = useState(0.01)
  const [zoom, setZoom] = useState(1)
  const [busy, setBusy] = useState(false)
  const [conflict, setConflict] = useState<ApiProblem | null>(null)
  const [mappingProfileId, setMappingProfileId] = useState('')
  const [mappingWorkSiteId, setMappingWorkSiteId] = useState('')
  const [mappingBusy, setMappingBusy] = useState(false)
  const canvasRef = useRef<HTMLDivElement>(null)
  const dragRef = useRef<DragState | null>(null)
  const selectedSlot = slots.data?.find((item) => item.id === selectedId) ?? null
  const editable = layout?.status === 'DRAFT'
  const selectedMapping = rdsMappings.data?.find((item) => item.slotId === selectedId) ?? null
  const rdsWorkSites = useQuery({
    queryKey: ['rds-worksites', mappingProfileId],
    queryFn: ({ signal }) => api.rdsWorkSites({ navigationProfileId: mappingProfileId, type: 1 }, signal),
    enabled: !!mappingProfileId,
  })

  const slotParts = useQuery({
    queryKey: ['slot-parts', selectedId],
    queryFn: ({ signal }) => api.slotParts(selectedId!, signal),
    enabled: !!selectedId,
  })

  // Detect dirty state for beforeunload warning and concurrency freeze
  const isDirty = useMemo(() => {
    if (!selectedSlot || !working) return false
    const base = toDraft(selectedSlot)
    return JSON.stringify(base) !== JSON.stringify(working)
  }, [selectedSlot, working])

  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => {
      if (isDirty) {
        e.preventDefault()
        e.returnValue = ''
      }
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [isDirty])

  const displaySlots = useMemo(
    () => (slots.data ?? []).map((slot) => (slot.id === selectedId && working ? { ...slot, ...working } : slot)),
    [selectedId, slots.data, working],
  )

  const overlapIds = useMemo(() => {
    const ids = new Set<string>()
    const candidates = displaySlots.filter((slot) => slot.isEnabled && placed(slot))
    for (let first = 0; first < candidates.length; first += 1) {
      for (let second = first + 1; second < candidates.length; second += 1) {
        const a = candidates[first]
        const b = candidates[second]
        if (a.x! < b.x! + b.width! && a.x! + a.width! > b.x! && a.y! < b.y! + b.height! && a.y! + a.height! > b.y!) {
          ids.add(a.id)
          ids.add(b.id)
        }
      }
    }
    return ids
  }, [displaySlots])

  useEffect(() => {
    const move = (event: PointerEvent) => {
      const drag = dragRef.current
      if (!drag) return
      const dx = (event.clientX - drag.startX) / drag.bounds.width
      const dy = (event.clientY - drag.startY) / drag.bounds.height
      setWorking((current) => {
        if (!current || !placed(drag.base)) return current
        if (drag.mode === 'move') {
          return {
            ...current,
            x: snapValue(clamp(drag.base.x! + dx, 0, 1 - drag.base.width!), grid),
            y: snapValue(clamp(drag.base.y! + dy, 0, 1 - drag.base.height!), grid),
          }
        }
        return {
          ...current,
          width: snapValue(clamp(drag.base.width! + dx, grid, 1 - drag.base.x!), grid),
          height: snapValue(clamp(drag.base.height! + dy, grid, 1 - drag.base.y!), grid),
        }
      })
    }
    const up = () => {
      dragRef.current = null
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    return () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
    }
  }, [grid])

  useEffect(() => {
    const preferred =
      selectedMapping?.navigationProfileId ??
      rdsProfiles.data?.find((item) => item.status === 'PUBLISHED')?.id ??
      rdsProfiles.data?.[0]?.id ??
      ''
    setMappingProfileId(preferred)
    setMappingWorkSiteId(selectedMapping?.rdsWorkSiteId ?? '')
  }, [selectedMapping?.navigationProfileId, selectedMapping?.rdsWorkSiteId, rdsProfiles.data])

  const choose = (slot: Slot) => {
    setConflict(null)
    setSelectedId(slot.id)
    setWorking(toDraft(slot))
  }

  const startDrag = (slot: Slot, mode: DragState['mode'], event: ReactPointerEvent) => {
    if (!editable || !canvasRef.current || !placed(slot)) return
    event.preventDefault()
    event.stopPropagation()
    choose(slot)
    dragRef.current = {
      mode,
      startX: event.clientX,
      startY: event.clientY,
      bounds: canvasRef.current.getBoundingClientRect(),
      base: toDraft(slot),
    }
  }

  const nudge = (event: React.KeyboardEvent) => {
    if (!editable || !working || !placed(working) || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return
    event.preventDefault()
    const step = event.shiftKey ? grid * 5 : grid
    setWorking({
      ...working,
      x: clamp(working.x! + (event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0), 0, 1 - working.width!),
      y: clamp(working.y! + (event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0), 0, 1 - working.height!),
    })
  }

  const refresh = async () => {
    await Promise.all([
      client.invalidateQueries({ queryKey: ['layouts'] }),
      client.invalidateQueries({ queryKey: ['slots', layoutId] }),
    ])
  }

  const reloadAfterConflict = async () => {
    setBusy(true)
    try {
      await refresh()
      setSelectedId(null)
      setWorking(null)
      setConflict(null)
      toast.success('Sunucu kaydı yenilendi')
    } finally {
      setBusy(false)
    }
  }

  const save = async () => {
    if (!layout || !selectedSlot || !working || !!conflict) return
    setBusy(true)
    try {
      const { code: _code, ...payload } = working
      await api.updateSlot(layout, selectedSlot, payload)
      await refresh()
      toast.success('Slot kaydedildi', `${working.code} · normalize geometri güncellendi`)
      setSelectedId(null)
      setWorking(null)
      setConflict(null)
    } catch (error) {
      toast.error(error, 'Slot kaydedilemedi')
      if (error instanceof ApiProblem && error.isConcurrencyConflict) {
        setConflict(error)
        // Concurrency Guard: DO NOT refresh queries here. Refreshing would update
        // the concurrencyToken while keeping the dirty draft, allowing a silent overwrite
        // on a second save click. The user must explicitly reload via reloadAfterConflict.
      } else {
        await refresh()
      }
    } finally {
      setBusy(false)
    }
  }

  const create = async (draft: Omit<SlotDraft, 'isEnabled'>) => {
    if (!layout) return
    setBusy(true)
    try {
      const result = await api.createSlot(layout, draft)
      await refresh()
      toast.success('Slot oluşturuldu')
      setCreateOpen(false)
      setSelectedId(result.id)
      setWorking({ ...draft, isEnabled: true })
    } catch (error) {
      toast.error(error)
      await refresh()
    } finally {
      setBusy(false)
    }
  }

  const assignPart = async (partId: string) => {
    if (!selectedSlot) return
    try {
      await api.assignSlotPart(selectedSlot.id, partId)
      await client.invalidateQueries({ queryKey: ['slot-parts', selectedSlot.id] })
      toast.success('Parça slota atandı')
    } catch (error) {
      toast.error(error, 'Parça atanamadı')
    }
  }

  const removePart = async (partId: string) => {
    if (!selectedSlot) return
    try {
      await api.removeSlotPart(selectedSlot.id, partId)
      await client.invalidateQueries({ queryKey: ['slot-parts', selectedSlot.id] })
      toast.success('Parça slot ataması kaldırıldı')
    } catch (error) {
      toast.error(error, 'Parça ataması kaldırılamadı')
    }
  }

  const setMapping = async () => {
    if (!selectedSlot || !mappingWorkSiteId) return
    setMappingBusy(true)
    try {
      await api.setRdsSlotMapping(selectedSlot.id, mappingWorkSiteId)
      await client.invalidateQueries({ queryKey: ['rds-mappings', layoutId] })
      toast.success('RDS WorkSite eşlendi', 'Eşleme TKG içinde audit kaydıyla saklandı; RDS değiştirilmedi.')
    } catch (error) {
      toast.error(error, 'RDS eşlemesi kaydedilemedi')
    } finally {
      setMappingBusy(false)
    }
  }

  const removeMapping = async () => {
    if (!selectedSlot) return
    setMappingBusy(true)
    try {
      await api.removeRdsSlotMapping(selectedSlot.id)
      await client.invalidateQueries({ queryKey: ['rds-mappings', layoutId] })
      setMappingWorkSiteId('')
      toast.success('RDS WorkSite eşlemesi kaldırıldı')
    } catch (error) {
      toast.error(error, 'RDS eşlemesi kaldırılamadı')
    } finally {
      setMappingBusy(false)
    }
  }

  const all = [layouts, slots, machines, containers, parts]
  if (all.some((query) => query.isLoading)) return <LoadingState label="Normalize layout editörü hazırlanıyor…" />
  const failure = all.find((query) => query.error)?.error
  if (failure) return <ErrorState error={failure} onRetry={() => all.forEach((query) => void query.refetch())} />
  if (!layout) {
    return (
      <EmptyState
        title="Layout bulunamadı"
        description="İstenen layout mevcut değil veya erişim dışında."
        action={
          <Link to="/layouts" className="button button--secondary button--md">
            <ArrowLeft size={16} /> Layout listesi
          </Link>
        }
      />
    )
  }

  const unplaced = displaySlots.filter((slot) => !placed(slot))
  return (
    <div className="page page--editor">
      <div className="editor-title">
        <Link to="/layouts" className="back-link">
          <ArrowLeft size={17} /> Layoutlar
        </Link>
        <div>
          <span className="eyebrow">{machine?.code} · {layout.code}</span>
          <h1>{layout.name} <span>v{layout.version}</span></h1>
        </div>
        <StatusPill value={layout.status} />
        {editable ? (
          <Button disabled={!selectedSlot || !working || busy || !!conflict} onClick={save}>
            <Save size={17} /> Seçili slotu kaydet
          </Button>
        ) : (
          <span className="read-only">
            <LockKeyhole size={16} /> Salt okunur
          </span>
        )}
      </div>

      <div className="warning-banner warning-banner--editor">
        <strong>Robot Haritası Değildir</strong>
        <span>Normalize geometri yalnız fiziksel/ekran yerleşimi içindir. Robot X/Y, waypoint, rota, map veya stitch konfigürasyonu üretmez.</span>
      </div>

      <div className="editor-toolbar">
        <div>
          <Button size="sm" variant="secondary" disabled={!editable} onClick={() => setCreateOpen(true)}>
            <Plus size={15} /> Slot ekle
          </Button>
          <span className="toolbar-divider" />
          <Grid3X3 size={16} />
          <SelectInput aria-label="Grid hassasiyeti" value={grid} onChange={(event) => setGrid(Number(event.target.value))}>
            <option value="0.02">%2 grid</option>
            <option value="0.01">%1 grid</option>
            <option value="0.005">%0,5 grid</option>
          </SelectInput>
        </div>
        <div>
          <Button size="sm" variant="ghost" onClick={() => setZoom((value) => clamp(value - 0.1, 0.7, 1.5))}>
            <ZoomOut size={16} />
          </Button>
          <span>{Math.round(zoom * 100)}%</span>
          <Button size="sm" variant="ghost" onClick={() => setZoom((value) => clamp(value + 0.1, 0.7, 1.5))}>
            <ZoomIn size={16} />
          </Button>
        </div>
      </div>

      {conflict && (
        <div className="overlap-alert conflict-alert" role="alert">
          <AlertTriangle />
          <div>
            <strong>Eşzamanlılık Çakışması (409 Conflict)</strong>
            <span>Bu slot veya layout başka bir kullanıcı/yönetici tarafından değiştirildi. Kaydetme kilitlendi; mevcut form değerleriniz kaybolmadı. Sunucudaki son durumu incelemek için yeniden yükleyin.</span>
          </div>
          <Button size="sm" variant="secondary" disabled={busy} onClick={() => void reloadAfterConflict()}>
            Sunucudan yeniden yükle
          </Button>
        </div>
      )}

      {overlapIds.size > 0 && (
        <div className="overlap-alert">
          <AlertTriangle />
          <div>
            <strong>{overlapIds.size} slot çakışıyor</strong>
            <span>Turuncu işaretli alanları yayın öncesinde doğrulayın. Editör otomatik yer değiştirme yapmaz.</span>
          </div>
        </div>
      )}

      <div className="editor-layout">
        <Panel className="canvas-panel">
          <div className="canvas-scroll">
            <div
              className="layout-canvas"
              ref={canvasRef}
              tabIndex={0}
              onKeyDown={nudge}
              style={{ transform: `scale(${zoom})`, transformOrigin: 'top left' }}
              data-testid="layout-canvas"
            >
              <div className="canvas-grid" />
              <div className="machine-axis machine-axis--x">Normalize X →</div>
              <div className="machine-axis machine-axis--y">Normalize Y →</div>
              {displaySlots.filter(placed).map((slot) => (
                <button
                  type="button"
                  key={slot.id}
                  data-testid={`slot-${slot.code}`}
                  aria-label={`${slot.code} · ${slot.displayName} · ${slot.role === 'MACHINE' ? 'taşıma dışı referans' : slot.role}`}
                  className={`layout-slot layout-slot--${slot.role.toLowerCase()} ${slot.id === selectedId ? 'selected' : ''} ${overlapIds.has(slot.id) ? 'overlap' : ''} ${!slot.isEnabled ? 'disabled' : ''}`}
                  style={{
                    left: `${slot.x! * 100}%`,
                    top: `${slot.y! * 100}%`,
                    width: `${slot.width! * 100}%`,
                    height: `${slot.height! * 100}%`,
                  }}
                  onClick={() => choose(slot)}
                  onPointerDown={(event) => startDrag(slot, 'move', event)}
                >
                  <span>{slot.code}</span>
                  <small>{slot.displayName}</small>
                  <em>{slot.role === 'MACHINE' ? 'MACHINE · taşıma dışı' : slot.role}</em>
                  {editable && slot.id === selectedId && (
                    <i className="resize-handle" onPointerDown={(event) => startDrag(slot, 'resize', event)} />
                  )}
                </button>
              ))}
              {!displaySlots.some(placed) && (
                <div className="canvas-empty">
                  <BoxSelect />
                  <strong>Yerleştirilmiş slot yok</strong>
                  <span>NULL geometrili bir slot seçip “Canvas’a yerleştir” kullanın.</span>
                </div>
              )}
            </div>
          </div>
          <div className="canvas-legend">
            {roles.map((role) => (
              <span key={role}>
                <i className={`legend-dot legend-dot--${role.toLowerCase()}`} /> {role}
              </span>
            ))}
          </div>
        </Panel>

        <Panel className="inspector">
          <div className="inspector__head">
            <div>
              <span className="eyebrow">Slot inspector</span>
              <h2>{selectedSlot?.code ?? 'Slot seçin'}</h2>
            </div>
            {selectedSlot && <StatusPill value={selectedSlot.isEnabled ? 'ACTIVE' : 'INACTIVE'} />}
          </div>

          {selectedSlot && working ? (
            <>
              <div className="definition-grid">
                <Definition label="Concurrency" value={selectedSlot.concurrencyToken} />
                <Definition label="Layout ETag" value={layout.concurrencyToken} />
              </div>
              <div className="inspector-form">
                <Field label="Görünen ad">
                  <TextInput
                    disabled={!editable}
                    value={working.displayName}
                    onChange={(event) => setWorking({ ...working, displayName: event.target.value })}
                  />
                </Field>
                <Field label="Slot rolü">
                  <SelectInput
                    disabled={!editable}
                    value={working.role}
                    onChange={(event) => setWorking({ ...working, role: event.target.value as SlotRole })}
                  >
                    {roles.map((role) => (
                      <option key={role}>{role}</option>
                    ))}
                  </SelectInput>
                </Field>
                <Field label="Default kasa">
                  <SelectInput
                    disabled={!editable}
                    value={working.defaultContainerTypeId ?? ''}
                    onChange={(event) => setWorking({ ...working, defaultContainerTypeId: event.target.value || null })}
                  >
                    <option value="">Bilinmiyor / yok</option>
                    {containers.data?.map((item) => (
                      <option value={item.id} key={item.id}>
                        {item.code} · {item.name}
                      </option>
                    ))}
                  </SelectInput>
                </Field>
                <Field label="Görünüm sırası">
                  <TextInput
                    disabled={!editable}
                    type="number"
                    value={working.displayOrder}
                    onChange={(event) => setWorking({ ...working, displayOrder: Number(event.target.value) })}
                  />
                </Field>
                <div className="geometry-grid">
                  {(['x', 'y', 'width', 'height'] as const).map((key) => (
                    <Field label={key.toUpperCase()} key={key}>
                      <TextInput
                        disabled={!editable}
                        type="number"
                        min={0}
                        max={1}
                        step={grid}
                        value={working[key] ?? ''}
                        onChange={(event) =>
                          setWorking({
                            ...working,
                            [key]: event.target.value === '' ? null : Number(event.target.value),
                          })
                        }
                      />
                    </Field>
                  ))}
                </div>
                <div className="check-stack">
                  <Check
                    disabled={!editable}
                    label="Boş yük kabul eder"
                    checked={working.allowsEmptyLoad}
                    onChange={(value) => setWorking({ ...working, allowsEmptyLoad: value })}
                  />
                  <Check
                    disabled={!editable}
                    label="Dolu yük kabul eder"
                    checked={working.allowsFullLoad}
                    onChange={(value) => setWorking({ ...working, allowsFullLoad: value })}
                  />
                  <Check
                    disabled={!editable}
                    label="Aktif slot"
                    checked={working.isEnabled}
                    onChange={(value) => setWorking({ ...working, isEnabled: value })}
                  />
                </div>
                {!placed(working) && editable && (
                  <Button
                    variant="secondary"
                    onClick={() => setWorking({ ...working, x: 0.1, y: 0.1, width: 0.16, height: 0.12 })}
                  >
                    <MousePointer2 size={16} /> Canvas’a yerleştir
                  </Button>
                )}
              </div>

              <SlotPartAssociationInspector
                slot={selectedSlot}
                slotParts={slotParts.data ?? []}
                allParts={parts.data ?? []}
                editable={editable}
                mapping={selectedMapping}
                onAssign={assignPart}
                onRemove={removePart}
              />

              <RdsMappingInspector
                slot={selectedSlot}
                mapping={selectedMapping}
                profiles={rdsProfiles.data ?? []}
                workSites={rdsWorkSites.data ?? []}
                profileId={mappingProfileId}
                workSiteId={mappingWorkSiteId}
                busy={mappingBusy}
                loading={rdsProfiles.isLoading || rdsMappings.isLoading || rdsWorkSites.isLoading}
                error={rdsProfiles.error ?? rdsMappings.error ?? rdsWorkSites.error}
                onProfileChange={(value) => {
                  setMappingProfileId(value)
                  setMappingWorkSiteId('')
                }}
                onWorkSiteChange={setMappingWorkSiteId}
                onSave={() => void setMapping()}
                onRemove={() => void removeMapping()}
              />
            </>
          ) : (
            <EmptyState title="Slot seçilmedi" description="Canvas veya yerleşmemiş slot listesinden bir kayıt seçin." />
          )}
        </Panel>
      </div>

      {unplaced.length > 0 && (
        <Panel>
          <div className="panel-header">
            <div>
              <h2>Konum Koordinatı Bekleyen İstasyonlar</h2>
              <p>Henüz yerleşim koordinatı atanmamış istasyon ve slot kayıtları.</p>
            </div>
            <StatusPill value={`${unplaced.length} KONUM`} tone="warning" />
          </div>
          <div className="unplaced-list">
            {unplaced.map((slot) => (
              <button key={slot.id} onClick={() => choose(slot)}>
                <Minus />
                <span>
                  <strong>{slot.code}</strong>
                  <small>{slot.displayName} · {slot.role}</small>
                </span>
              </button>
            ))}
          </div>
        </Panel>
      )}

      {createOpen && (
        <CreateSlotDialog
          open
          busy={busy}
          containers={containers.data ?? []}
          onClose={() => setCreateOpen(false)}
          onSave={create}
        />
      )}
    </div>
  )
}

function SlotPartAssociationInspector({
  slot,
  slotParts,
  allParts,
  editable,
  mapping,
  onAssign,
  onRemove,
}: {
  slot: Slot
  slotParts: SlotPartRecord[]
  allParts: Part[]
  editable: boolean
  mapping: RdsSlotMapping | null
  onAssign: (partId: string) => Promise<void>
  onRemove: (partId: string) => Promise<void>
}) {
  const [selectedPartId, setSelectedPartId] = useState('')
  const [busy, setBusy] = useState(false)

  const availableParts = useMemo(() => {
    const assignedIds = new Set(slotParts.map((p) => p.partId ?? p.id))
    return allParts.filter((p) => p.isActive && !assignedIds.has(p.id))
  }, [allParts, slotParts])

  const assign = async () => {
    if (!selectedPartId) return
    setBusy(true)
    try {
      await onAssign(selectedPartId)
      setSelectedPartId('')
    } finally {
      setBusy(false)
    }
  }

  const remove = async (partId: string) => {
    setBusy(true)
    try {
      await onRemove(partId)
    } finally {
      setBusy(false)
    }
  }

  // Transport metadata check
  const stationType = (slot.metadata?.station_type ?? slot.metadata?.stationType ?? null) as string | null
  const dockType = (slot.metadata?.dock_type ?? slot.metadata?.dockType ?? null) as string | null
  const heightMm = (slot.metadata?.height_mm ?? slot.metadata?.height ?? null) as string | number | null

  const hasParts = slotParts.length > 0
  const isMissingCommissioning = hasParts && (!mapping?.rdsWorkSiteId || !mapping?.executionReady)

  return (
    <div className="slot-parts-inspector">
      <span className="eyebrow">Operasyonel Yönlendirme (master.layout_slot_parts)</span>
      <div className="slot-parts-header">
        <strong>Atanmış Parçalar ({slotParts.length})</strong>
        <small>Otomatik makine-slot çözümleme bu eşleşmeleri kullanır.</small>
      </div>

      {isMissingCommissioning && (
        <div className="warning-banner warning-banner--slot-commissioning" role="alert">
          <AlertTriangle size={15} />
          <span>
            <strong>Eksik Devreye Alma Uyarısı:</strong> Parça atanmış bu slot için geçerli bir RDS WorkSite eşlemesi bulunmuyor.
          </span>
        </div>
      )}

      <div className="transport-metadata-box">
        <span className="metadata-title">Taşıma Metadata Bilgileri</span>
        <div className="metadata-tags">
          <span className={`meta-tag ${stationType ? 'meta-tag--set' : 'meta-tag--missing'}`}>
            İstasyon: {stationType ?? 'Tanımsız'}
          </span>
          <span className={`meta-tag ${dockType ? 'meta-tag--set' : 'meta-tag--missing'}`}>
            Dock: {dockType ?? 'Tanımsız'}
          </span>
          <span className={`meta-tag ${heightMm != null ? 'meta-tag--set' : 'meta-tag--missing'}`}>
            Yükseklik: {heightMm != null ? `${heightMm} mm` : 'Tanımsız'}
          </span>
        </div>
      </div>

      <div className="slot-parts-list">
        {slotParts.length === 0 ? (
          <p className="slot-parts-empty">Atanmış parça yok (genel buffer veya serbest slot).</p>
        ) : (
          <ul className="slot-parts-items">
            {slotParts.map((part) => {
              const partId = part.partId ?? part.id!
              return (
                <li key={partId} className="slot-part-item">
                  <div className="slot-part-info">
                    <strong>{part.partCode}</strong>
                    {part.description && <small>{part.description}</small>}
                  </div>
                  {editable && (
                    <Button
                      size="sm"
                      variant="ghost"
                      disabled={busy}
                      onClick={() => void remove(partId)}
                      aria-label={`${part.partCode} eşlemesini kaldır`}
                    >
                      <Trash2 size={14} />
                    </Button>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </div>

      {editable && (
        <div className="slot-part-add-form">
          <SelectInput
            disabled={busy || availableParts.length === 0}
            value={selectedPartId}
            onChange={(e) => setSelectedPartId(e.target.value)}
            aria-label="Atanacak parça"
          >
            <option value="">Parça seçin ({availableParts.length} uygun)</option>
            {availableParts.map((p) => (
              <option key={p.id} value={p.id}>
                {p.code} {p.description ? `· ${p.description}` : ''}
              </option>
            ))}
          </SelectInput>
          <Button
            size="sm"
            variant="secondary"
            disabled={!selectedPartId || busy}
            onClick={() => void assign()}
          >
            <Plus size={14} /> Ata
          </Button>
        </div>
      )}
    </div>
  )
}

function RdsMappingInspector({
  slot,
  mapping,
  profiles,
  workSites,
  profileId,
  workSiteId,
  busy,
  loading,
  error,
  onProfileChange,
  onWorkSiteChange,
  onSave,
  onRemove,
}: {
  slot: Slot
  mapping: RdsSlotMapping | null
  profiles: RdsNavigationProfile[]
  workSites: RdsWorkSite[]
  profileId: string
  workSiteId: string
  busy: boolean
  loading: boolean
  error: unknown
  onProfileChange: (value: string) => void
  onWorkSiteChange: (value: string) => void
  onSave: () => void
  onRemove: () => void
}) {
  return (
    <div className="rds-mapping-inspector">
      <span className="eyebrow">RDS WorkSite</span>
      {slot.role === 'MACHINE' ? (
        <div className="mapping-forbidden">
          <LockKeyhole size={16} />
          <span>
            <strong>Makine alanı — RDS taşıma noktası eşlenemez</strong>
            <small>Bu bölge yalnız fiziksel/ekran yerleşim referansıdır.</small>
          </span>
        </div>
      ) : (
        <>
          {mapping?.rdsSiteId && (
            <div className="mapping-current">
              <span>Mevcut eşleme</span>
              <strong>{mapping.rdsSiteId}</strong>
              <StatusPill value={mapping.executionReady ? 'READY' : 'NOT READY'} tone={mapping.executionReady ? 'success' : 'warning'} />
            </div>
          )}
          {error ? (
            <ErrorState error={error} />
          ) : (
            <div className="mapping-fields">
              <Field label="Navigation profile">
                <SelectInput disabled={busy || loading} value={profileId} onChange={(event) => onProfileChange(event.target.value)}>
                  <option value="">Profil seçin</option>
                  {profiles.map((profile) => (
                    <option key={profile.id} value={profile.id}>
                      {profile.machineCode} · {profile.code} v{profile.version}
                    </option>
                  ))}
                </SelectInput>
              </Field>
              <Field label="Physical WorkSite">
                <SelectInput disabled={!profileId || busy || loading} value={workSiteId} onChange={(event) => onWorkSiteChange(event.target.value)}>
                  <option value="">Eşleme seçin</option>
                  {workSites.map((site) => (
                    <option key={site.id} value={site.id}>
                      {site.siteId}
                      {site.executionReady ? '' : ' · NOT READY'}
                    </option>
                  ))}
                </SelectInput>
              </Field>
              <div className="row-actions">
                <Button size="sm" disabled={!workSiteId || busy} onClick={onSave}>
                  {mapping ? 'Eşlemeyi değiştir' : 'Eşle'}
                </Button>
                {mapping && (
                  <Button size="sm" variant="danger" disabled={busy} onClick={onRemove}>
                    Eşlemeyi kaldır
                  </Button>
                )}
              </div>
              <small>Ad benzerliği otomatik eşleme oluşturmaz. Seçim TKG audit kaydıyla saklanır ve RDS'e yazılmaz.</small>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function CreateSlotDialog({
  open,
  busy,
  containers,
  onClose,
  onSave,
}: {
  open: boolean
  busy: boolean
  containers: { id: string; code: string; name: string }[]
  onClose: () => void
  onSave: (draft: Omit<SlotDraft, 'isEnabled'>) => void
}) {
  const [draft, setDraft] = useState<Omit<SlotDraft, 'isEnabled'>>({
    code: '',
    displayName: '',
    role: 'STAGING',
    defaultContainerTypeId: null,
    x: 0.1,
    y: 0.1,
    width: 0.16,
    height: 0.12,
    displayOrder: 0,
    allowsEmptyLoad: true,
    allowsFullLoad: true,
    metadata: {},
  })
  const submit = (event: FormEvent) => {
    event.preventDefault()
    onSave(draft)
  }
  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Draft layout’a slot ekle"
      description="Kod bu layout sürümü içinde immutable olacaktır."
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Vazgeç
          </Button>
          <Button type="submit" form="slot-create" disabled={busy}>
            <Plus size={16} /> Slot oluştur
          </Button>
        </>
      }
    >
      <form id="slot-create" className="form-grid" onSubmit={submit}>
        <Field label="Slot kodu">
          <TextInput required value={draft.code} onChange={(event) => setDraft({ ...draft, code: event.target.value })} />
        </Field>
        <Field label="Görünen ad">
          <TextInput required value={draft.displayName} onChange={(event) => setDraft({ ...draft, displayName: event.target.value })} />
        </Field>
        <Field label="Rol">
          <SelectInput value={draft.role} onChange={(event) => setDraft({ ...draft, role: event.target.value as SlotRole })}>
            {roles.map((role) => (
              <option key={role}>{role}</option>
            ))}
          </SelectInput>
        </Field>
        <Field label="Default kasa">
          <SelectInput
            value={draft.defaultContainerTypeId ?? ''}
            onChange={(event) => setDraft({ ...draft, defaultContainerTypeId: event.target.value || null })}
          >
            <option value="">Bilinmiyor / yok</option>
            {containers.map((item) => (
              <option value={item.id} key={item.id}>
                {item.code} · {item.name}
              </option>
            ))}
          </SelectInput>
        </Field>
        <div className="form-span geometry-grid">
          {(['x', 'y', 'width', 'height'] as const).map((key) => (
            <Field label={key.toUpperCase()} key={key}>
              <TextInput
                type="number"
                min={0}
                max={1}
                step="0.01"
                value={draft[key] ?? ''}
                onChange={(event) => setDraft({ ...draft, [key]: event.target.value === '' ? null : Number(event.target.value) })}
              />
            </Field>
          ))}
        </div>
        <div className="form-span role-grid">
          <Check label="Boş yük" checked={draft.allowsEmptyLoad} onChange={(value) => setDraft({ ...draft, allowsEmptyLoad: value })} />
          <Check label="Dolu yük" checked={draft.allowsFullLoad} onChange={(value) => setDraft({ ...draft, allowsFullLoad: value })} />
        </div>
      </form>
    </Modal>
  )
}
