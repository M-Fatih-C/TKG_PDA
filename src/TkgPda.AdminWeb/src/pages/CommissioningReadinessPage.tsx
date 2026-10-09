import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useAdminApi } from '../api/ApiContext'
import type { OperationCapability } from '../api/types'
import { ErrorState, LoadingState, PageHeader, Panel } from '../components/ui'

const operationLabels: Record<string, string> = {
  BRING_FULL: 'Malzeme Getir',
  BRING_EMPTY: 'Boş Kasa Getir',
  REMOVE_FULL_MAMUL: 'Mamul Kasası Götür',
  REMOVE_FULL_ALT_RETURN: 'Alt / Yarı Mamul Dönüşü',
  REMOVE_EMPTY: 'Boş Kasa Götür',
}

const blockerText: Record<string, string> = {
  NAVIGATION_PROFILE_NOT_PUBLISHED: 'NAV yayında değil',
  GEOMETRY_NOT_VERIFIED: 'Geometri doğrulanmadı',
  SOURCE_LAYOUT_MISSING: 'Kaynak yerleşim yok',
  PENDING_SOURCE_LAYOUT: 'Kaynak yerleşim bekleniyor',
  TASK_TEMPLATE_MISSING: 'Görev şablonu yok',
  PREPARATION_WORKSITE_MAPPING_MISSING: 'Hazırlık alanı bağlı değil',
  EMPTY_SOURCE_MAPPING_MISSING: 'Boş kasa kaynağı bağlı değil',
  EMPTY_RETURN_MAPPING_MISSING: 'Boş kasa dönüş alanı eksik',
  FINISHED_GOODS_MAPPING_MISSING: 'FG alan eşleşmesi eksik',
  NON_FINISHED_RETURN_MAPPING_MISSING: 'Alt dönüş alanı eksik',
  BINDING_INCOMPLETE: 'Makine konum eşleşmesi eksik',
  RDS_WORKSITE_NOT_DISCOVERED: 'WorkSite henüz keşfedilmedi',
  RDS_WORKSITE_DISABLED: 'WorkSite kapalı',
  RDS_WORKSITE_SYNC_FAILED: 'WorkSite senkronu başarısız',
  PROFILE_MISMATCH: 'NAV profili uyuşmuyor',
  WORKSITE_DISABLED: 'WorkSite kapalı',
  WORKSITE_SYNC_FAILED: 'WorkSite senkronu başarısız',
}

const overallText: Record<string, string> = {
  READY: 'Hazır',
  PARTIALLY_READY: 'Kısmen hazır',
  NOT_READY: 'Hazır değil',
}

function capabilityText(operation: OperationCapability) {
  if (operation.state === 'READY') return 'Hazır'
  const text = operation.blockers.map((code) => blockerText[code] ?? code)
  return text[0] ?? 'Eksik'
}

export function CommissioningReadinessPage() {
  const api = useAdminApi()
  const query = useQuery({
    queryKey: ['commissioning-readiness'],
    queryFn: ({ signal }) => api.commissioningReadiness(signal),
  })
  const [open, setOpen] = useState<string | null>(null)

  if (query.isLoading) return <LoadingState label="Devreye alma hazırlığı yükleniyor…" />
  if (query.error) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />

  return (
    <div className="page">
      <PageHeader
        eyebrow="RDS"
        title="Devreye alma hazırlığı"
        description="Bir işlem ancak şablonu, NAV profili ve doğrulanmış fiziksel uç noktası varken hazır görünür. Taslak profil ve doğrulanmamış geometri robot görevi açmaz."
      />
      <div className="readiness-grid">
        {query.data?.map((row) => {
          const key = `${row.machineCode}-${row.variantCode}`
          const operations = row.operations
          return (
            <Panel key={key}>
              <div className="readiness-meta">
                <strong>{row.machineCode}</strong>
                <span>Varyant {row.variantCode}</span>
                <span>Harita {row.mapName ?? '—'}</span>
                <span>NAV {row.navigationCode}</span>
                <span>Durum {row.profileStatus === 'PUBLISHED' ? 'Yayında' : 'Taslak'}</span>
                <span>Geometri {row.geometryVerified ? 'Doğrulandı' : 'Doğrulanmadı'}</span>
                <span>Task şablonları {row.activeTaskTemplates}</span>
                <span>{overallText[row.overallState] ?? row.overallState}</span>
              </div>
              <ul className="capability-list">
                {operations.map((operation) => (
                  <li key={operation.operation} className={operation.state === 'READY' ? 'capability capability--ready' : 'capability capability--blocked'}>
                    <span>{operationLabels[operation.operation] ?? operation.operation}</span>
                    <b>{operation.state === 'READY' ? '✓ Hazır' : `⚠ ${capabilityText(operation)}`}</b>
                  </li>
                ))}
              </ul>
              <details className="readiness-detail" open={open === key} onToggle={(event) => setOpen(event.currentTarget.open ? key : null)}>
                <summary>Teknik ayrıntı</summary>
                <ul>
                  {row.operations.flatMap((operation) => operation.blockers.map((code) => (
                    <li key={`${operation.operation}-${code}`}>{operationLabels[operation.operation] ?? operation.operation}: {blockerText[code] ?? code} ({code})</li>
                  )))}
                </ul>
              </details>
            </Panel>
          )
        })}
      </div>
    </div>
  )
}
