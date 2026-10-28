import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { renderAuthenticated } from '../test/render'
import { AppShell } from './AppShell'

describe('AppShell navigation', () => {
  it('renders active admin navigation links and excludes deprecated configuration links', () => {
    renderAuthenticated(
      <AppShell>
        <div>Test Page Content</div>
      </AppShell>,
    )

    // MUST be present in navigation
    expect(screen.getByRole('link', { name: /Genel Bakış/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Kullanıcılar & Yetkiler/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Cihazlar/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Makine & Varyantlar/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Denetim Geçmişi/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Üretim Sonu Sırası/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /RDS Entegrasyonu/i })).toBeInTheDocument()

    // MUST NOT be present in navigation (WP-01)
    expect(screen.queryByRole('link', { name: /Fiziksel Layoutlar/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Parça & Gruplar/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Kasalar/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Veri Kalitesi/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Devreye Alma Hazırlığı/i })).not.toBeInTheDocument()
  })
})
