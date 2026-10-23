import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { ProductionEndAreaPriority } from '../api/types'
import { Button, EmptyState, ErrorState, LoadingState, PageHeader, Panel, SearchInput, SelectInput, TableFrame } from '../components/ui'

const roleLabels: Record<string, string> = {
  MAIN_PART: 'Ana parça',
  ALT_PART: 'Alternatif parça',
  RELATED: 'Alt parça',
  CONSUMABLE: 'Sarf parça',
  OTHER: 'Diğer parça',
}

const rowKey = (row: ProductionEndAreaPriority) => `${row.productGroupId}:${row.layoutSlotId}:${row.partId}`

export function ProductionEndPriorityPage() {
  const api = useAdminApi()
  const queryClient = useQueryClient()
  const [machineId, setMachineId] = useState('')
  const [variantId, setVariantId] = useState('')
  const [search, setSearch] = useState('')
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const query = useQuery({
    queryKey: ['production-end-area-priorities'],
    queryFn: ({ signal }) => api.productionEndAreaPriorities(signal),
  })
  const mutation = useMutation({
    mutationFn: (input: ProductionEndAreaPriority & { nextPriority: number | null }) =>
      api.setProductionEndAreaPriority(input.productGroupId, input.layoutSlotId, input.partId, input.nextPriority),
    onSuccess: (data, input) => {
      queryClient.setQueryData(['production-end-area-priorities'], data)
      setDrafts((current) => {
        const next = { ...current }
        delete next[rowKey(input)]
        return next
      })
      setError(null)
    },
    onError: (reason: Error) => setError(reason.message),
  })

  const rows = useMemo(() => query.data ?? [], [query.data])
  const machines = useMemo(() => Array.from(new Map(rows.map((row) => [row.machineId, { id: row.machineId, code: row.machineCode, name: row.machineName }])).values()), [rows])
  const variants = useMemo(() => Array.from(new Map(rows.filter((row) => !machineId || row.machineId === machineId).map((row) => [row.variantId, { id: row.variantId, code: row.variantCode, name: row.variantName }])).values()), [rows, machineId])
  const filtered = useMemo(() => {
    const needle = search.trim().toLocaleLowerCase('tr-TR')
    return rows.filter((row) => {
      if (machineId && row.machineId !== machineId) return false
      if (variantId && row.variantId !== variantId) return false
      if (!needle) return true
      return [row.machineCode, row.machineName, row.variantCode, row.variantName, row.mainPartCode, row.partCode, row.slotCode, row.slotName, row.layoutCode, row.layoutName]
        .some((value) => value.toLocaleLowerCase('tr-TR').includes(needle))
    })
  }, [rows, machineId, variantId, search])

  const save = (row: ProductionEndAreaPriority, value: string) => {
    const trimmed = value.trim()
    if (trimmed === '') {
      mutation.mutate({ ...row, nextPriority: null })
      return
    }
    const priority = Number(trimmed)
    if (!Number.isInteger(priority) || priority < 1) {
      setError('Öncelik 1 veya daha büyük bir tam sayı olmalıdır.')
      return
    }
    mutation.mutate({ ...row, nextPriority: priority })
  }

  if (query.isLoading) return <LoadingState label="Alan öncelikleri yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />

  return (
    <div className="page page--editor">
      <PageHeader
        eyebrow="Operasyon Yapılandırması"
        title="Alan Bazlı Üretim Bitiş Öncelikleri"
        description="Üretim bittiğinde kaldırılacak paletlerin sırasını makine, varyant, parça ve fiziksel alana göre belirleyin. Küçük sayı önce yürütülür."
      />
      <Panel>
        <div className="production-priority-help">
          Yalnızca onaylı parça–alan eşleşmeleri listelenir. Alan önceliği boş bırakılırsa paletin dolu veya boş olmasına göre mevcut onaylı işlem önceliği kullanılır.
        </div>
        {error && <p className="production-priority-error" role="alert">{error}</p>}
        <div className="production-priority-toolbar">
          <SearchInput value={search} onChange={setSearch} placeholder="Makine, varyant, parça veya alan ara…" />
          <SelectInput aria-label="Makine filtresi" value={machineId} onChange={(event) => { setMachineId(event.target.value); setVariantId('') }}>
            <option value="">Tüm makineler</option>
            {machines.map((machine) => <option key={machine.id} value={machine.id}>{machine.code} — {machine.name}</option>)}
          </SelectInput>
          <SelectInput aria-label="Varyant filtresi" value={variantId} onChange={(event) => setVariantId(event.target.value)}>
            <option value="">Tüm varyantlar</option>
            {variants.map((variant) => <option key={variant.id} value={variant.id}>{variant.code} — {variant.name}</option>)}
          </SelectInput>
          <span>{filtered.length} eşleşme</span>
        </div>
        {filtered.length ? (
          <TableFrame>
            <thead>
              <tr>
                <th>Makine</th>
                <th>Varyant</th>
                <th>Ürün / Ana Parça</th>
                <th>Parça Türü</th>
                <th>Parça</th>
                <th>Fiziksel Alan</th>
                <th>Öncelik</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {filtered.map((row) => {
                const key = rowKey(row)
                const value = drafts[key] ?? (row.priority?.toString() ?? '')
                return (
                  <tr key={key}>
                    <td><strong>{row.machineCode}</strong><small className="table-subtitle">{row.machineName}</small></td>
                    <td><strong>{row.variantCode}</strong><small className="table-subtitle">{row.variantName}</small></td>
                    <td><strong>{row.mainPartCode}</strong><small className="table-subtitle">{row.layoutName}</small></td>
                    <td>{roleLabels[row.partRole] ?? 'Alt parça'}</td>
                    <td><strong>{row.partCode}</strong></td>
                    <td><strong>{row.slotName}</strong><small className="table-subtitle">{row.slotCode}</small></td>
                    <td>
                      <input
                        className="input production-priority-input"
                        aria-label={`${row.machineCode} ${row.variantCode} ${row.partCode} ${row.slotCode} önceliği`}
                        inputMode="numeric"
                        min={1}
                        placeholder="Varsayılan"
                        value={value}
                        onChange={(event) => setDrafts({ ...drafts, [key]: event.target.value })}
                      />
                    </td>
                    <td><Button size="sm" variant="secondary" disabled={mutation.isPending} onClick={() => save(row, value)}>Kaydet</Button></td>
                  </tr>
                )
              })}
            </tbody>
          </TableFrame>
        ) : <EmptyState title="Eşleşme bulunamadı" description="Filtreleri değiştirin veya Makine & Varyantlar ekranındaki onaylı parça–alan eşleşmelerini kontrol edin." />}
      </Panel>
    </div>
  )
}
