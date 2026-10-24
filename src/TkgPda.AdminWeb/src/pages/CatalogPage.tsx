import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Boxes, Link2, Pencil, Plus, Save, Trash2 } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { ContainerType, Part, PartRelationType, ProductGroup, Variant } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, Check, EmptyState, ErrorState, Field, LoadingState, Modal, PageHeader, Panel, SearchInput, SelectInput, StatusPill, TableFrame, TextInput } from '../components/ui'

export function CatalogPage() {
  const api = useAdminApi()
  const toast = useToast()
  const client = useQueryClient()
  const parts = useQuery({ queryKey: ['parts'], queryFn: ({ signal }) => api.parts(signal) })
  const groups = useQuery({ queryKey: ['product-groups'], queryFn: ({ signal }) => api.productGroups(undefined, signal) })
  const variants = useQuery({ queryKey: ['variants'], queryFn: ({ signal }) => api.variants(undefined, signal) })
  const containers = useQuery({ queryKey: ['containers'], queryFn: ({ signal }) => api.containers(signal) })
  const [tab, setTab] = useState<'parts' | 'groups'>('parts')
  const [search, setSearch] = useState('')
  const [selectedVariantId, setSelectedVariantId] = useState('')
  const [partDialog, setPartDialog] = useState<Part | 'new' | null>(null)
  const [groupDialog, setGroupDialog] = useState<ProductGroup | 'new' | null>(null)
  const [relationGroup, setRelationGroup] = useState<ProductGroup | null>(null)
  const [busy, setBusy] = useState(false)
  const refresh = async () => { await Promise.all([client.invalidateQueries({ queryKey: ['parts'] }), client.invalidateQueries({ queryKey: ['product-groups'] })]) }
  const execute = async (action: () => Promise<unknown>, message: string, close?: () => void) => { setBusy(true); try { await action(); await refresh(); toast.success(message); close?.() } catch (error) { toast.error(error); await refresh() } finally { setBusy(false) } }
  const filteredParts = useMemo(() => (parts.data ?? []).filter((part) => `${part.code} ${part.description ?? ''}`.toLowerCase().includes(search.toLowerCase())), [parts.data, search])
  const filteredGroups = useMemo(() => (groups.data ?? []).filter((group) => (!selectedVariantId || group.variantId === selectedVariantId) && `${group.mainPartCode} ${group.name ?? ''}`.toLowerCase().includes(search.toLowerCase())), [groups.data, search, selectedVariantId])
  const all = [parts, groups, variants, containers]
  if (all.some((query) => query.isLoading)) return <LoadingState label="Parça ve ürün grupları yükleniyor…" />
  const failure = all.find((query) => query.error)?.error
  if (failure) return <ErrorState error={failure} onRetry={() => all.forEach((query) => void query.refetch())} />
  const variantLabel = (id: string) => { const value = variants.data?.find((item) => item.id === id); return value ? `${value.code} · ${value.name}` : '—' }

  const relationLabel = (type: PartRelationType) => {
    switch (type) {
      case 'ALT_PART': return 'Alt Parça'
      case 'RELATED': return 'İlgili Parça'
      case 'CONSUMABLE': return 'Sarf Malzeme'
      default: return 'Diğer'
    }
  }

  return <div className="page">
    <PageHeader eyebrow="Ana Veri Yönetimi" title="Parça & Ürün Grupları" description="Parçaları varyant bağlamında ana ürün, alt parça ve ilgili parçalar olarak hiyerarşik yapılandırın." actions={<Button onClick={() => tab === 'parts' ? setPartDialog('new') : setGroupDialog('new')}><Plus size={17} /> {tab === 'parts' ? 'Parça ekle' : 'Ürün grubu ekle'}</Button>} />
    <div className="segmented">
      <button className={tab === 'parts' ? 'active' : ''} onClick={() => setTab('parts')}>Parça Kataloğu <span>{parts.data?.length}</span></button>
      <button className={tab === 'groups' ? 'active' : ''} onClick={() => setTab('groups')}>Ürün Grupları <span>{groups.data?.length}</span></button>
    </div>
    <Panel>
      <div className="table-toolbar table-toolbar--split">
        <div className="toolbar-group">
          <SearchInput value={search} onChange={setSearch} placeholder={tab === 'parts' ? 'Parça kodu veya açıklama ara…' : 'Ana parça veya grup ara…'} />
          {tab === 'groups' && (
            <SelectInput value={selectedVariantId} onChange={(e) => setSelectedVariantId(e.target.value)}>
              <option value="">Tüm varyantlar</option>
              {variants.data?.map((v) => <option value={v.id} key={v.id}>{v.code} · {v.name}</option>)}
            </SelectInput>
          )}
        </div>
        <span>{tab === 'parts' ? filteredParts.length : filteredGroups.length} kayıt</span>
      </div>
      {tab === 'parts' ? <>
        {filteredParts.length ? <TableFrame>
          <thead><tr><th>Parça Kodu</th><th>Açıklama</th><th>Durum</th><th className="align-right">İşlem</th></tr></thead>
          <tbody>{filteredParts.map((part) => <tr key={part.id}>
            <td><code className="mono-code">{part.code}</code></td>
            <td>{part.description ?? <span className="muted">Açıklama yok</span>}</td>
            <td><StatusPill value={part.isActive ? 'ACTIVE' : 'INACTIVE'} /></td>
            <td><div className="row-actions"><Button size="sm" variant="secondary" onClick={() => setPartDialog(part)}><Pencil size={15} /> Düzenle</Button></div></td>
          </tr>)}</tbody>
        </TableFrame> : <EmptyState title="Parça bulunamadı" description="Aramayı değiştirin veya canonical parça ekleyin." />}
      </> : <>
        {filteredGroups.length ? <div className="group-list">
          {filteredGroups.map((group) => <article className="group-card" key={group.id}>
            <header>
              <div className="group-card__header-content">
                <div className="group-card__badges">
                  <span className="variant-tag">{variantLabel(group.variantId)}</span>
                  <span className="main-part-tag">Ana Parça: <code>{group.mainPartCode}</code></span>
                </div>
                <h3>{group.name ? `${group.name} (${group.mainPartCode})` : group.mainPartCode}</h3>
              </div>
              <div className="group-card__actions">
                <StatusPill value={group.isActive ? 'ACTIVE' : 'INACTIVE'} />
                <Button size="sm" variant="ghost" onClick={() => setGroupDialog(group)} aria-label="Grubu düzenle"><Pencil size={15} /></Button>
              </div>
            </header>
            <div className="group-parts">
              <span className="group-parts__title">Tanımlı Alt &amp; İlgili Parçalar ({group.parts.length}):</span>
              {group.parts.length ? group.parts.map((part) => <div key={part.partId} className="group-part-item">
                <Boxes size={16} />
                <span className="group-part-item__info">
                  <code className="mono-code">{part.partCode}</code>
                  <span className={`relation-badge relation-badge--${part.relationType.toLowerCase()}`}>
                    {relationLabel(part.relationType)}
                  </span>
                  <small>
                    {part.containerCode ? `Kasa: ${part.containerCode}` : 'Kasa tanımsız'} · Lot: {part.lotQuantity ?? 'Standart'}
                  </small>
                </span>
                <StatusPill value={part.isActive ? 'ACTIVE' : 'INACTIVE'} />
                <Button aria-label={`${part.partCode} ilişkisini kaldır`} size="sm" variant="ghost" onClick={() => execute(() => api.removeProductGroupPart(group, part.partId), 'Parça ilişkisi kaldırıldı')}><Trash2 size={15} /></Button>
              </div>) : <span className="muted">Tanımlı alt parça ilişkisi yok.</span>}
            </div>
            <footer>
              <Button size="sm" variant="secondary" onClick={() => setRelationGroup(group)}><Link2 size={15} /> Parça ilişkilendir</Button>
              <span>{group.parts.length} alt/ilgili parça · Sıra {group.displayOrder}</span>
            </footer>
          </article>)}
        </div> : <EmptyState title="Ürün grubu bulunamadı" description="Aramayı veya varyant filtresini değiştirin ya da yeni ürün grubu oluşturun." />}
      </>}
    </Panel>
    {partDialog && <PartDialog part={partDialog === 'new' ? undefined : partDialog} busy={busy} onClose={() => setPartDialog(null)} onSave={(draft) => execute(() => partDialog === 'new' ? api.createPart({ code: draft.code, description: draft.description }) : api.updatePart(partDialog, { description: draft.description, isActive: draft.isActive }), partDialog === 'new' ? 'Parça oluşturuldu' : 'Parça güncellendi', () => setPartDialog(null))} />}
    {groupDialog && <GroupDialog group={groupDialog === 'new' ? undefined : groupDialog} variants={variants.data ?? []} parts={parts.data ?? []} busy={busy} onClose={() => setGroupDialog(null)} onSave={(draft) => execute(() => groupDialog === 'new' ? api.createProductGroup({ variantId: draft.variantId, mainPartId: draft.mainPartId, name: draft.name || null, displayOrder: draft.displayOrder }) : api.updateProductGroup(groupDialog, { name: draft.name || null, displayOrder: draft.displayOrder, isActive: draft.isActive }), groupDialog === 'new' ? 'Ürün grubu oluşturuldu' : 'Ürün grubu güncellendi', () => setGroupDialog(null))} />}
    {relationGroup && <RelationDialog group={relationGroup} parts={parts.data ?? []} containers={containers.data ?? []} busy={busy} onClose={() => setRelationGroup(null)} onSave={(partId, draft) => execute(() => api.setProductGroupPart(relationGroup, partId, draft), 'Parça ilişkisi kaydedildi', () => setRelationGroup(null))} />}
  </div>
}

function PartDialog({ part, busy, onClose, onSave }: { part?: Part; busy: boolean; onClose: () => void; onSave: (draft: Pick<Part, 'code' | 'description' | 'isActive'>) => void }) {
  const [draft, setDraft] = useState({ code: part?.code ?? '', description: part?.description ?? '', isActive: part?.isActive ?? true })
  return <Modal open onClose={onClose} title={part ? `${part.code} düzenle` : 'Canonical parça ekle'} description="Parça kodu oluşturulduktan sonra değiştirilemez." footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button disabled={busy || !draft.code} onClick={() => onSave({ ...draft, description: draft.description || null })}><Save size={16} /> Kaydet</Button></>}><div className="form-grid"><Field label="Parça kodu"><TextInput disabled={!!part} value={draft.code} onChange={(event) => setDraft({ ...draft, code: event.target.value })} /></Field><Field label="Durum"><Check label="Aktif parça" checked={draft.isActive} onChange={(isActive) => setDraft({ ...draft, isActive })} /></Field><div className="form-span"><Field label="Açıklama"><TextInput value={draft.description} onChange={(event) => setDraft({ ...draft, description: event.target.value })} /></Field></div></div></Modal>
}

interface GroupDraft { variantId: string; mainPartId: string; name: string; displayOrder: number; isActive: boolean }
function GroupDialog({ group, variants, parts, busy, onClose, onSave }: { group?: ProductGroup; variants: Variant[]; parts: Part[]; busy: boolean; onClose: () => void; onSave: (draft: GroupDraft) => void }) {
  const [draft, setDraft] = useState<GroupDraft>({ variantId: group?.variantId ?? variants[0]?.id ?? '', mainPartId: group?.mainPartId ?? parts[0]?.id ?? '', name: group?.name ?? '', displayOrder: group?.displayOrder ?? 0, isActive: group?.isActive ?? true })
  const submit = (event: FormEvent) => { event.preventDefault(); onSave(draft) }
  return <Modal open onClose={onClose} title={group ? `${group.mainPartCode} grubunu düzenle` : 'Ürün grubu oluştur'} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button type="submit" form="group-form" disabled={busy || !draft.variantId || !draft.mainPartId}><Save size={16} /> Kaydet</Button></>}><form id="group-form" className="form-grid" onSubmit={submit}><Field label="Varyant"><SelectInput disabled={!!group} value={draft.variantId} onChange={(event) => setDraft({ ...draft, variantId: event.target.value })}>{variants.map((variant) => <option value={variant.id} key={variant.id}>{variant.code} · {variant.name}</option>)}</SelectInput></Field><Field label="Ana parça"><SelectInput disabled={!!group} value={draft.mainPartId} onChange={(event) => setDraft({ ...draft, mainPartId: event.target.value })}>{parts.filter((part) => part.isActive).map((part) => <option value={part.id} key={part.id}>{part.code}</option>)}</SelectInput></Field><Field label="Grup adı"><TextInput value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></Field><Field label="Sıra"><TextInput type="number" min={0} value={draft.displayOrder} onChange={(event) => setDraft({ ...draft, displayOrder: Number(event.target.value) })} /></Field>{group && <div className="form-span"><Check label="Aktif ürün grubu" checked={draft.isActive} onChange={(isActive) => setDraft({ ...draft, isActive })} /></div>}</form></Modal>
}

function RelationDialog({ group, parts, containers, busy, onClose, onSave }: { group: ProductGroup; parts: Part[]; containers: ContainerType[]; busy: boolean; onClose: () => void; onSave: (partId: string, draft: Record<string, unknown>) => void }) {
  const available = parts.filter((part) => part.isActive && part.id !== group.mainPartId)
  const [partId, setPartId] = useState(available[0]?.id ?? '')
  const [containerTypeId, setContainerTypeId] = useState('')
  const [relationType, setRelationType] = useState<PartRelationType>('ALT_PART')
  const [lotQuantity, setLotQuantity] = useState('')
  const [displayOrder, setDisplayOrder] = useState(group.parts.length)
  return <Modal open onClose={onClose} title="Parça ilişkilendir" description={`${group.name || group.mainPartCode} · Eksik lot/kasa değeri NULL kalabilir.`} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button disabled={busy || !partId} onClick={() => onSave(partId, { containerTypeId: containerTypeId || null, relationType, lotQuantity: lotQuantity === '' ? null : Number(lotQuantity), displayOrder, isActive: true })}><Link2 size={16} /> İlişkilendir</Button></>}><div className="form-grid"><Field label="İlişkilendirilecek Parça"><SelectInput value={partId} onChange={(event) => setPartId(event.target.value)}>{available.map((part) => <option value={part.id} key={part.id}>{part.code} · {part.description}</option>)}</SelectInput></Field><Field label="Kasa (Container)"><SelectInput value={containerTypeId} onChange={(event) => setContainerTypeId(event.target.value)}><option value="">Bilinmiyor (NULL)</option>{containers.filter((item) => item.isActive).map((item) => <option value={item.id} key={item.id}>{item.code} · {item.name}</option>)}</SelectInput></Field><Field label="İlişki Türü"><SelectInput value={relationType} onChange={(event) => setRelationType(event.target.value as PartRelationType)}><option value="ALT_PART">Alt Parça (ALT_PART)</option><option value="RELATED">İlgili Parça (RELATED)</option><option value="CONSUMABLE">Sarf Malzeme (CONSUMABLE)</option><option value="OTHER">Diğer (OTHER)</option></SelectInput></Field><Field label="Lot Miktarı"><TextInput type="number" min={0} step="any" placeholder="NULL (Standart)" value={lotQuantity} onChange={(event) => setLotQuantity(event.target.value)} /></Field><Field label="Sıra"><TextInput type="number" min={0} value={displayOrder} onChange={(event) => setDisplayOrder(Number(event.target.value))} /></Field></div></Modal>
}
