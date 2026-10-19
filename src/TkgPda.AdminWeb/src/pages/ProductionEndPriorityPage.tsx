import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useAdminApi } from '../api/ApiContext'
import { ErrorState, LoadingState, PageHeader, Panel } from '../components/ui'

const labels: Record<string, string> = {
  BRING_EMPTY: 'Boş kasa getir',
  BRING_FULL: 'Dolu malzeme getir',
  REMOVE_FULL: 'Dolu kasa kaldır',
  REMOVE_EMPTY: 'Boş kasa kaldır',
}

export function ProductionEndPriorityPage() {
  const api = useAdminApi()
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: ['production-end-priorities'],
    queryFn: ({ signal }) => api.productionEndPriorities(signal),
  })
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const mutation = useMutation({
    mutationFn: (input: { operationCode: string; priority: number | null }) =>
      api.setProductionEndPriority(input.operationCode, input.priority),
    onSuccess: (data) => {
      queryClient.setQueryData(['production-end-priorities'], data)
      setError(null)
    },
    onError: (reason: Error) => setError(reason.message),
  })

  if (query.isLoading) return <LoadingState label="Üretim bitiş öncelikleri yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />

  return (
    <div className="page">
      <PageHeader
        eyebrow="Operasyon Yapılandırması"
        title="Üretim Bitiş Öncelikleri"
        description="Üretimi Bitir işlemi ile oluşturulan taşıma görevlerinin yürütme öncelikleri buradan yönetilir."
      />
      <Panel>
        <p>
          Her taşıma işlemi için işletme öncelik sırasını belirleyebilirsiniz. Değer girilmeyen işlemler varsayılan kuyruk önceliğiyle yürütülür.
        </p>
        {error && <p role="alert">{error}</p>}
        <table>
          <thead>
            <tr>
              <th>İşlem</th>
              <th>Öncelik</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(query.data ?? []).map((row) => {
              const value = drafts[row.operationCode] ?? (row.priority?.toString() ?? '')
              return (
                <tr key={row.operationCode}>
                  <td>
                    <strong>{labels[row.operationCode] ?? row.operationCode}</strong>
                    <small className="table-subtitle">{row.operationCode}</small>
                  </td>
                  <td>
                    <input
                      aria-label={`${row.operationCode} önceliği`}
                      inputMode="numeric"
                      value={value}
                      onChange={(event) => setDrafts({ ...drafts, [row.operationCode]: event.target.value })}
                    />
                  </td>
                  <td>
                    <button
                      type="button"
                      disabled={mutation.isPending}
                      onClick={() => {
                        const trimmed = value.trim()
                        if (trimmed === '') {
                          mutation.mutate({ operationCode: row.operationCode, priority: null })
                          return
                        }
                        const priority = Number(trimmed)
                        if (!Number.isInteger(priority) || priority < 1) {
                          setError('Öncelik 1 veya daha büyük bir tam sayı olmalıdır.')
                          return
                        }
                        mutation.mutate({ operationCode: row.operationCode, priority })
                      }}
                    >
                      Kaydet
                    </button>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </Panel>
    </div>
  )
}
