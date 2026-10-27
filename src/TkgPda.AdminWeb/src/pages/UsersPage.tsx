import { useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, Plus, Save, Shield, UserCog } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { useAdminApi } from '../api/ApiContext'
import type { AdminUser, MachinePermission, Role, RoleCode, UserDraft } from '../api/types'
import { useToast } from '../components/Toast'
import { Button, Check, EmptyState, ErrorState, Field, LoadingState, Modal, PageHeader, Panel, SearchInput, StatusPill, TableFrame, TextInput, formatDate } from '../components/ui'
import { roleLabel } from '../displayLabels'

const emptyUser: UserDraft = { username: '', password: '', fullName: '', employeeNo: null, roles: ['LINE_OPERATOR'] }

export function UsersPage() {
  const api = useAdminApi()
  const toast = useToast()
  const queryClient = useQueryClient()
  const users = useQuery({ queryKey: ['users'], queryFn: ({ signal }) => api.users(signal) })
  const roles = useQuery({ queryKey: ['roles'], queryFn: ({ signal }) => api.roles(signal) })
  const machines = useQuery({ queryKey: ['machines'], queryFn: ({ signal }) => api.machines(signal) })
  const [search, setSearch] = useState('')
  const [createOpen, setCreateOpen] = useState(false)
  const [editId, setEditId] = useState<string | null>(null)
  const [resetId, setResetId] = useState<string | null>(null)
  const [permissionId, setPermissionId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const filtered = useMemo(() => (users.data ?? []).filter((user) => `${user.username} ${user.fullName} ${user.employeeNo ?? ''}`.toLocaleLowerCase('tr').includes(search.toLocaleLowerCase('tr'))), [search, users.data])
  const selected = (id: string | null) => users.data?.find((user) => user.id === id) ?? null
  const refresh = async () => { await queryClient.invalidateQueries({ queryKey: ['users'] }) }
  const execute = async (action: () => Promise<unknown>, success: string, close?: () => void) => {
    setBusy(true)
    try { await action(); await refresh(); toast.success(success); close?.() } catch (error) { toast.error(error); await refresh() } finally { setBusy(false) }
  }

  if (users.isLoading || roles.isLoading || machines.isLoading) return <LoadingState label="Kullanıcı ve yetki modeli yükleniyor…" />
  const failure = users.error ?? roles.error ?? machines.error
  if (failure) return <ErrorState error={failure} onRetry={() => { void users.refetch(); void roles.refetch(); void machines.refetch() }} />

  return (
    <div className="page">
      <PageHeader eyebrow="Kimlik ve erişim" title="Kullanıcılar & yetkiler" description="Bireysel hesapları, sistem rollerini ve makine bazlı izinleri tek yerden yönetin." actions={<Button onClick={() => setCreateOpen(true)}><Plus size={17} /> Kullanıcı oluştur</Button>} />
      <Panel>
        <div className="table-toolbar"><SearchInput value={search} onChange={setSearch} placeholder="Ad, kullanıcı adı veya sicil ara…" /><span>{filtered.length} kayıt</span></div>
        {filtered.length ? <TableFrame><thead><tr><th>Kullanıcı</th><th>Sicil No</th><th>Sistem Rolleri</th><th>Makine İzni</th><th>Son Giriş</th><th>Durum</th><th className="align-right">İşlemler</th></tr></thead><tbody>
          {filtered.map((user) => <tr key={user.id}>
            <td><div className="identity-cell"><span>{user.fullName.slice(0, 2).toUpperCase()}</span><div><strong>{user.fullName}</strong><small>@{user.username}</small></div></div></td>
            <td>{user.employeeNo ? <code className="mono-code">{user.employeeNo}</code> : <span className="muted">—</span>}</td>
            <td><div className="pill-list">{user.roles.map((role) => <StatusPill key={role} value={role} tone={role === 'ADMIN' ? 'info' : 'neutral'} />)}</div></td>
            <td>{user.roles.includes('ADMIN') ? <span className="perm-chip perm-chip--admin">Tüm Makineler</span> : user.machinePermissions.length > 0 ? <span className="perm-chip">{user.machinePermissions.length} makine</span> : <span className="muted">Tanımsız</span>}</td>
            <td>{formatDate(user.lastLoginAt)}</td>
            <td><StatusPill value={user.isActive ? 'ACTIVE' : 'INACTIVE'} /></td>
            <td><div className="row-actions"><Button size="sm" variant="ghost" onClick={() => setPermissionId(user.id)}><Shield size={15} /> Yetkiler</Button><Button size="sm" variant="ghost" onClick={() => setResetId(user.id)}><KeyRound size={15} /> Parola</Button><Button size="sm" variant="secondary" onClick={() => setEditId(user.id)}><UserCog size={15} /> Düzenle</Button></div></td>
          </tr>)}
        </tbody></TableFrame> : <EmptyState title="Kullanıcı bulunamadı" description="Arama ölçütünü değiştirin veya yeni kullanıcı oluşturun." />}
      </Panel>

      {createOpen && <CreateUserDialog open roles={roles.data ?? []} busy={busy} onClose={() => setCreateOpen(false)} onSubmit={(draft) => execute(() => api.createUser(draft), 'Kullanıcı oluşturuldu', () => setCreateOpen(false))} />}
      {selected(editId) && <EditUserDialog key={selected(editId)!.id} user={selected(editId)!} roles={roles.data ?? []} busy={busy} onClose={() => setEditId(null)} onSubmit={(user, profile, nextRoles) => execute(async () => {
        const updated = await api.updateUser(user, profile)
        if ([...updated.roles].sort().join() !== [...nextRoles].sort().join()) await api.setRoles(updated, nextRoles)
      }, 'Kullanıcı güncellendi', () => setEditId(null))} />}
      <PasswordDialog user={selected(resetId)} busy={busy} onClose={() => setResetId(null)} onSubmit={(user, password) => execute(() => api.resetPassword(user, password), 'Parola güvenli biçimde yenilendi', () => setResetId(null))} />
      <PermissionDialog user={selected(permissionId)} machines={machines.data ?? []} busy={busy} onClose={() => setPermissionId(null)} onChange={(user, machineId, key, checked) => execute(async () => {
        const current = user.machinePermissions.find((item) => item.machineId === machineId)
        const next = normalizePermission({ machineId, canView: false, canOperate: false, canChangeVariant: false, canManageSlots: false, ...current, [key]: checked }, key, checked)
        if (!next.canView && !next.canOperate && !next.canChangeVariant && !next.canManageSlots && current) await api.removePermission(user, machineId)
        else await api.setPermission(user, next)
      }, 'Makine yetkisi güncellendi')} />
    </div>
  )
}

function RoleChecks({ roles, value, onChange }: { roles: Role[]; value: RoleCode[]; onChange: (value: RoleCode[]) => void }) {
  return <div className="role-grid">{roles.filter((role) => role.isActive).map((role) => <Check key={role.code} label={roleLabel(role.code, role.name)} checked={value.includes(role.code)} onChange={(checked) => onChange(checked ? [...value, role.code] : value.filter((item) => item !== role.code))} />)}</div>
}

function CreateUserDialog({ open, roles, busy, onClose, onSubmit }: { open: boolean; roles: Role[]; busy: boolean; onClose: () => void; onSubmit: (draft: UserDraft) => void }) {
  const [draft, setDraft] = useState(emptyUser)
  const submit = (event: FormEvent) => { event.preventDefault(); onSubmit({ ...draft, employeeNo: draft.employeeNo || null }) }
  return <Modal open={open} onClose={onClose} title="Yeni kullanıcı" description="Üretim operatörleri sicil numarası ve yetkili cihazla giriş yapar. Parola yalnızca yönetim paneli ve eski kullanıcı adı girişleri içindir." footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button form="create-user" type="submit" disabled={busy || draft.roles.length === 0}><Save size={16} /> Oluştur</Button></>}>
    <form id="create-user" className="form-grid" onSubmit={submit}><Field label="Kullanıcı adı"><TextInput required value={draft.username} onChange={(event) => setDraft({ ...draft, username: event.target.value })} /></Field><Field label="Ad soyad"><TextInput required value={draft.fullName} onChange={(event) => setDraft({ ...draft, fullName: event.target.value })} /></Field><Field label="Sicil numarası"><TextInput value={draft.employeeNo ?? ''} onChange={(event) => setDraft({ ...draft, employeeNo: event.target.value })} /></Field><Field label="Geçici parola" hint="En az 12 karakter"><TextInput required type="password" minLength={12} value={draft.password} onChange={(event) => setDraft({ ...draft, password: event.target.value })} /></Field><div className="form-span"><Field label="Sistem rolleri"><RoleChecks roles={roles} value={draft.roles} onChange={(value) => setDraft({ ...draft, roles: value })} /></Field></div></form>
  </Modal>
}

function EditUserDialog({ user, roles, busy, onClose, onSubmit }: { user: AdminUser; roles: Role[]; busy: boolean; onClose: () => void; onSubmit: (user: AdminUser, profile: Pick<AdminUser, 'fullName' | 'employeeNo' | 'isActive'>, roles: RoleCode[]) => void }) {
  const [draft, setDraft] = useState(() => ({ fullName: user.fullName, employeeNo: user.employeeNo ?? '', isActive: user.isActive, roles: user.roles }))
  return <Modal open onClose={onClose} title={`${user.fullName} düzenle`} description={`@${user.username} · Kayıt sürümü ${user.concurrencyToken}`} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button disabled={busy || draft.roles.length === 0} onClick={() => onSubmit(user, { fullName: draft.fullName, employeeNo: draft.employeeNo || null, isActive: draft.isActive }, draft.roles)}><Save size={16} /> Kaydet</Button></>}>
    <div className="form-grid"><Field label="Ad soyad"><TextInput value={draft.fullName} onChange={(event) => setDraft({ ...draft, fullName: event.target.value })} /></Field><Field label="Sicil numarası"><TextInput value={draft.employeeNo} onChange={(event) => setDraft({ ...draft, employeeNo: event.target.value })} /></Field><div className="form-span"><Check label="Aktif kullanıcı" checked={draft.isActive} onChange={(value) => setDraft({ ...draft, isActive: value })} /></div><div className="form-span"><Field label="Sistem rolleri"><RoleChecks roles={roles} value={draft.roles} onChange={(value) => setDraft({ ...draft, roles: value })} /></Field></div></div>
  </Modal>
}

function PasswordDialog({ user, busy, onClose, onSubmit }: { user: AdminUser | null; busy: boolean; onClose: () => void; onSubmit: (user: AdminUser, password: string) => void }) {
  const [password, setPassword] = useState('')
  if (!user) return null
  return <Modal open onClose={onClose} title="Parola yenile" description={`${user.fullName} için yeni parola güvenli biçimde saklanacak; denetim kaydında parola yer almayacak.`} footer={<><Button variant="ghost" onClick={onClose}>Vazgeç</Button><Button disabled={busy || password.length < 12} onClick={() => onSubmit(user, password)}><KeyRound size={16} /> Parolayı yenile</Button></>}><Field label="Yeni parola" hint="12–256 karakter"><TextInput autoFocus type="password" minLength={12} maxLength={256} value={password} onChange={(event) => setPassword(event.target.value)} /></Field></Modal>
}

type PermissionKey = Exclude<keyof MachinePermission, 'machineId'>
const permissionColumns: { key: PermissionKey; label: string }[] = [{ key: 'canView', label: 'Görüntüle' }, { key: 'canOperate', label: 'Operasyon' }, { key: 'canChangeVariant', label: 'Varyant' }, { key: 'canManageSlots', label: 'Alan Yönetimi' }]

function normalizePermission(permission: MachinePermission, key: PermissionKey, checked: boolean): MachinePermission {
  if (key === 'canView' && !checked) {
    return { ...permission, canView: false, canOperate: false, canChangeVariant: false, canManageSlots: false }
  }
  if (key !== 'canView' && checked) return { ...permission, canView: true }
  return permission
}

function PermissionDialog({ user, machines, busy, onClose, onChange }: { user: AdminUser | null; machines: { id: string; code: string; displayName: string }[]; busy: boolean; onClose: () => void; onChange: (user: AdminUser, machineId: string, key: PermissionKey, checked: boolean) => void }) {
  if (!user) return null
  return <Modal open wide onClose={onClose} title="Makine izin matrisi" description={`${user.fullName} · Yönetici olmayan hesapların her isteği sunucu tarafından yeniden doğrulanır.`} footer={<Button onClick={onClose}>Tamam</Button>}><div className="permission-matrix" role="grid"><div className="permission-row permission-row--head"><strong>Makine / hücre</strong>{permissionColumns.map((item) => <strong key={item.key}>{item.label}</strong>)}</div>{machines.map((machine) => { const permission = user.machinePermissions.find((item) => item.machineId === machine.id); return <div className="permission-row" key={machine.id}><div><strong>{machine.code}</strong><small>{machine.displayName}</small></div>{permissionColumns.map(({ key, label }) => <Check key={key} label={label} disabled={busy || user.roles.includes('ADMIN')} checked={permission?.[key] ?? false} onChange={(checked) => onChange(user, machine.id, key, checked)} />)}</div> })}</div>{user.roles.includes('ADMIN') && <div className="info-banner">Yönetici rolü tüm makinelere erişebilir; matristeki değerler bu kullanıcı için uygulanmaz.</div>}</Modal>
}
