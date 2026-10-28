import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ListOrdered, RotateCcw, Save } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { ProductionEndAreaPriority } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, EmptyState, ErrorState, InfoBox, LoadingState, PageHeader, Panel, PanelHeader, SelectInput, StatusPill, TableFrame } from '../components/ui'

const roleLabels: Record<string, string> = {
  MAIN_PART: 'Ana parça',
  ALT_PART: 'Alternatif parça',
  RELATED: 'Alt parça',
  CONSUMABLE: 'Sarf parça',
  OTHER: 'Diğer parça',
}

const rowKey = (row: ProductionEndAreaPriority) => `${row.productGroupId}:${row.layoutSlotId}:${row.partId}`

interface SequenceGroup {
  key: string
  slotCode: string
  slotName: string
  rows: ProductionEndAreaPriority[]
  partCodes: string[]
  partRoles: string[]
  firstIndex: number
  priority: number | null
  hasPartSpecificOrder: boolean
}

export function ProductionEndPriorityPage() {
  const api = useAdminApi()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [machineId, setMachineId] = useState('')
  const [variantId, setVariantId] = useState('')
  const [productGroupId, setProductGroupId] = useState('')
  const [order, setOrder] = useState<string[]>([])
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const query = useQuery({
    queryKey: ['production-end-area-priorities'],
    queryFn: ({ signal }) => api.productionEndAreaPriorities(signal),
  })
  const defaults = useQuery({
    queryKey: ['production-end-priorities'],
    queryFn: ({ signal }) => api.productionEndPriorities(signal),
  })
  const mutation = useMutation({
    mutationFn: (input: { items: { productGroupId: string; layoutSlotId: string; partId: string; priority: number | null }[]; message: string }) =>
      api.setProductionEndAreaPriorities(input.items),
    onSuccess: (data, input) => {
      queryClient.setQueryData(['production-end-area-priorities'], data)
      setDrafts({})
      setDirty(false)
      setError(null)
      toast.success(input.message)
    },
    onError: (reason: Error) => setError(reason.message),
  })

  const rows = useMemo(() => query.data ?? [], [query.data])
  const machines = useMemo(() => Array.from(new Map(rows.map((row) => [row.machineId, { id: row.machineId, code: row.machineCode, name: row.machineName }])).values()), [rows])
  const variants = useMemo(() => Array.from(new Map(rows.filter((row) => row.machineId === machineId).map((row) => [row.variantId, { id: row.variantId, code: row.variantCode, name: row.variantName }])).values()), [rows, machineId])
  const productGroups = useMemo(() => Array.from(new Map(rows.filter((row) => row.machineId === machineId && row.variantId === variantId).map((row) => [row.productGroupId, { id: row.productGroupId, code: row.mainPartCode, layoutName: row.layoutName }])).values()), [rows, machineId, variantId])

  useEffect(() => {
    if (machines.length && !machines.some((machine) => machine.id === machineId)) setMachineId(machines[0].id)
  }, [machineId, machines])
  useEffect(() => {
    if (variants.length && !variants.some((variant) => variant.id === variantId)) setVariantId(variants[0].id)
  }, [variantId, variants])
  useEffect(() => {
    if (productGroups.length && !productGroups.some((group) => group.id === productGroupId)) setProductGroupId(productGroups[0].id)
  }, [productGroupId, productGroups])

  const selectedRows = useMemo(() => rows.filter((row) => row.machineId === machineId && row.variantId === variantId && row.productGroupId === productGroupId), [rows, machineId, variantId, productGroupId])
  const sequenceGroups = useMemo(() => {
    const grouped = new Map<string, SequenceGroup>()
    selectedRows.forEach((row, index) => {
      const key = `${row.productGroupId}:${row.layoutSlotId}`
      const current = grouped.get(key)
      if (current) {
        current.rows.push(row)
        if (!current.partCodes.includes(row.partCode)) current.partCodes.push(row.partCode)
        const label = roleLabels[row.partRole] ?? 'Alt parça'
        if (!current.partRoles.includes(label)) current.partRoles.push(label)
        return
      }
      grouped.set(key, {
        key,
        slotCode: row.slotCode,
        slotName: row.slotName,
        rows: [row],
        partCodes: [row.partCode],
        partRoles: [roleLabels[row.partRole] ?? 'Alt parça'],
        firstIndex: index,
        priority: row.priority,
        hasPartSpecificOrder: false,
      })
    })
    return Array.from(grouped.values()).map((group) => {
      const values = new Set(group.rows.map((row) => row.priority ?? 'default'))
      const configured = group.rows.map((row) => row.priority).filter((value): value is number => value !== null)
      return {
        ...group,
        priority: configured.length ? Math.min(...configured) : null,
        hasPartSpecificOrder: values.size > 1,
      }
    }).sort((left, right) => {
      if (left.priority !== null || right.priority !== null) {
        if (left.priority === null) return 1
        if (right.priority === null) return -1
        if (left.priority !== right.priority) return left.priority - right.priority
      }
      return left.firstIndex - right.firstIndex
    })
  }, [selectedRows])
  const sequenceSignature = sequenceGroups.map((group) => `${group.key}:${group.priority ?? 'default'}`).join('|')
  useEffect(() => {
    setOrder(sequenceGroups.map((group) => group.key))
    setDirty(false)
  }, [machineId, variantId, productGroupId, sequenceGroups, sequenceSignature])

  const orderedGroups = order.map((key) => sequenceGroups.find((group) => group.key === key)).filter((group): group is SequenceGroup => Boolean(group))
  const fullDefault = defaults.data?.find((item) => item.operationCode === 'REMOVE_FULL')?.priority
  const emptyDefault = defaults.data?.find((item) => item.operationCode === 'REMOVE_EMPTY')?.priority

  const move = (index: number, direction: -1 | 1) => {
    const target = index + direction
    if (target < 0 || target >= order.length) return
    setOrder((current) => {
      const next = [...current]
      ;[next[index], next[target]] = [next[target], next[index]]
      return next
    })
    setDirty(true)
  }

  const saveSequence = () => {
    const items = orderedGroups.flatMap((group, index) => group.rows.map((row) => ({
      productGroupId: row.productGroupId,
      layoutSlotId: row.layoutSlotId,
      partId: row.partId,
      priority: (index + 1) * 10,
    })))
    if (items.length) mutation.mutate({ items, message: 'Üretim sonu kaldırma sırası kaydedildi.' })
  }

  const resetSequence = () => {
    if (!selectedRows.length || !window.confirm('Seçili üretimin özel kaldırma sırası silinsin ve varsayılan işlem sırası kullanılsın mı?')) return
    mutation.mutate({
      items: selectedRows.map((row) => ({ productGroupId: row.productGroupId, layoutSlotId: row.layoutSlotId, partId: row.partId, priority: null })),
      message: 'Seçili üretim varsayılan kaldırma sırasına döndürüldü.',
    })
  }

  const saveException = (row: ProductionEndAreaPriority, value: string) => {
    const trimmed = value.trim()
    const priority = trimmed === '' ? null : Number(trimmed)
    if (priority !== null && (!Number.isInteger(priority) || priority < 1)) {
      setError('Öncelik 1 veya daha büyük bir tam sayı olmalıdır.')
      return
    }
    mutation.mutate({
      items: [{ productGroupId: row.productGroupId, layoutSlotId: row.layoutSlotId, partId: row.partId, priority }],
      message: priority === null ? 'Parçaya özel sıra kaldırıldı.' : 'Parçaya özel sıra kaydedildi.',
    })
  }

  if (query.isLoading) return <LoadingState label="Üretim sonu sırası yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />

  return (
    <div className="page page--editor">
      <PageHeader
        eyebrow="Operasyon Yapılandırması"
        title="Üretim Sonu Kaldırma Sırası"
        description="Üretim tamamlandığında robotun paletleri hangi sırayla kaldıracağını görsel olarak düzenleyin."
      />
      <InfoBox title="Nasıl kullanılır?">
        Makine, varyant ve üretilecek ana parçayı seçin; alanları yukarı veya aşağı taşıyın ve sıralamayı tek seferde kaydedin. Sistem gerekli sayısal öncelikleri arka planda güvenli olarak oluşturur.
      </InfoBox>
      <Panel>
        <div className="production-sequence-toolbar">
          <label><span>Makine</span><SelectInput aria-label="Makine seçimi" value={machineId} onChange={(event) => { setMachineId(event.target.value); setVariantId(''); setProductGroupId('') }}>{machines.map((machine) => <option key={machine.id} value={machine.id}>{machine.code} — {machine.name}</option>)}</SelectInput></label>
          <label><span>Varyant</span><SelectInput aria-label="Varyant seçimi" value={variantId} onChange={(event) => { setVariantId(event.target.value); setProductGroupId('') }}>{variants.map((variant) => <option key={variant.id} value={variant.id}>{variant.code} — {variant.name}</option>)}</SelectInput></label>
          <label><span>Ürün / Ana Parça</span><SelectInput aria-label="Ana parça seçimi" value={productGroupId} onChange={(event) => setProductGroupId(event.target.value)}>{productGroups.map((group) => <option key={group.id} value={group.id}>{group.code}</option>)}</SelectInput></label>
          <div className="production-sequence-defaults"><span>Varsayılanlar</span><strong>Dolu palet: {fullDefault ?? 'Tanımsız'} · Boş palet: {emptyDefault ?? 'Tanımsız'}</strong></div>
        </div>
        {error && <p className="production-priority-error" role="alert">{error}</p>}
        <PanelHeader
          title="Kaldırma sırası"
          description="Robot görevleri aşağıdaki sıraya göre kuyruğa alınır. Hazırlık, bağımlılık ve saha güvenliği kontrolleri uygulanmaya devam eder."
          action={<div className="row-actions"><Button variant="ghost" disabled={mutation.isPending || !selectedRows.length} onClick={resetSequence}><RotateCcw size={16} /> Varsayılana dön</Button><Button disabled={mutation.isPending || !dirty} onClick={saveSequence}><Save size={16} /> Sıralamayı kaydet</Button></div>}
        />
        {orderedGroups.length ? (
          <div className="production-sequence-list" aria-label="Üretim sonu kaldırma sırası">
            {orderedGroups.map((group, index) => (
              <article className="production-sequence-card" aria-label={`${index + 1}. ${group.slotName}`} key={group.key}>
                <span className="production-sequence-number">{index + 1}</span>
                <div className="production-sequence-content">
                  <div><strong>{group.slotName}</strong><small>{group.slotCode}</small></div>
                  <div className="production-sequence-parts">{group.partCodes.map((part) => <span key={part}>{part}</span>)}</div>
                  <div className="production-sequence-meta">{group.partRoles.join(' · ')} · {group.rows.length} onaylı eşleşme</div>
                </div>
                <div className="production-sequence-state">
                  {group.hasPartSpecificOrder ? <StatusPill value="Parçaya özel sıra" tone="warning" /> : group.priority === null ? <StatusPill value="Varsayılan" tone="neutral" /> : <StatusPill value="Sıralanmış" tone="success" />}
                  {dirty && <small>Yeni sıra: {(index + 1) * 10}</small>}
                </div>
                <div className="production-sequence-actions">
                  <Button size="sm" variant="secondary" aria-label={`${group.slotName} alanını yukarı taşı`} disabled={index === 0 || mutation.isPending} onClick={() => move(index, -1)}><ArrowUp size={16} /> Yukarı</Button>
                  <Button size="sm" variant="secondary" aria-label={`${group.slotName} alanını aşağı taşı`} disabled={index === orderedGroups.length - 1 || mutation.isPending} onClick={() => move(index, 1)}><ArrowDown size={16} /> Aşağı</Button>
                </div>
              </article>
            ))}
          </div>
        ) : <EmptyState title="Sıralanacak alan bulunamadı" description="Seçili üretim için onaylı parça–alan eşleşmesi bulunmuyor." />}
        {orderedGroups.length > 0 && <div className="production-sequence-note"><ListOrdered size={17} /><span>Sıralamayı kaydetmek, her fiziksel alanın sırasını o alandaki tüm onaylı parçalara uygular. Özel bir parça farklı sırada kaldırılacaksa aşağıdaki gelişmiş bölümü kullanın.</span></div>}
      </Panel>

      {selectedRows.length > 0 && <details className="production-exceptions">
        <summary>Gelişmiş: parçaya özel sıra tanımları</summary>
        <div className="production-exceptions__help">Bu bölüm yalnızca aynı fiziksel alandaki belirli bir parçanın diğerlerinden farklı sırada kaldırılması gerektiğinde kullanılmalıdır. Alan sırasını değiştirdikten sonra istisnayı yeniden kaydedebilirsiniz.</div>
        <TableFrame>
          <thead><tr><th>Fiziksel Alan</th><th>Parça Türü</th><th>Parça</th><th>Özel Sıra</th><th /></tr></thead>
          <tbody>{selectedRows.map((row) => {
            const key = rowKey(row)
            const value = drafts[key] ?? (row.priority?.toString() ?? '')
            return <tr key={key}><td><strong>{row.slotName}</strong><small className="table-subtitle">{row.slotCode}</small></td><td>{roleLabels[row.partRole] ?? 'Alt parça'}</td><td><strong>{row.partCode}</strong></td><td><input className="input production-priority-input" aria-label={`${row.partCode} ${row.slotCode} parçaya özel sırası`} inputMode="numeric" min={1} placeholder="Varsayılan" value={value} onChange={(event) => setDrafts({ ...drafts, [key]: event.target.value })} /></td><td><Button size="sm" variant="secondary" disabled={mutation.isPending} onClick={() => saveException(row, value)}>Kaydet</Button></td></tr>
          })}</tbody>
        </TableFrame>
      </details>}
    </div>
  )
}
