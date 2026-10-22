import { ArrowRight, Boxes, KeyRound, LayoutTemplate, LockKeyhole, ShieldCheck } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { ApiProblem } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Button, TextInput } from '../components/ui'

export function LoginPage() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(username, password)
      const from = (location.state as { from?: string } | null)?.from
      navigate(from && from !== '/login' ? from : '/', { replace: true })
    } catch (reason) {
      setError(reason instanceof ApiProblem ? reason.message : 'Yönetim oturumu açılamadı.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login-page">
      <section className="login-story">
        <div className="login-brand"><span>TKG</span><strong>PDA</strong></div>
        <div className="login-story__content">
          <span className="eyebrow eyebrow--light">Fabrika lojistiğinin doğrulanmış modeli</span>
          <h1>Operasyon gerçeğini<br />tek merkezden yönetin.</h1>
          <p>Kullanıcılar, yetkiler, varyantlar ve fiziksel yerleşim planları tek bir güvenli yapılandırma ekranında.</p>
          <div className="login-features">
            <div><ShieldCheck /><span><strong>Rol tabanlı erişim</strong><small>Yönetim paneli yetkisi</small></span></div>
            <div><LayoutTemplate /><span><strong>Sürümlü yerleşim planı</strong><small>Taslak → Yayın → Arşiv</small></span></div>
            <div><Boxes /><span><strong>Kanıta dayalı veri</strong><small>Tahmin yok, izlenebilirlik var</small></span></div>
          </div>
        </div>
        <small>TKG PDA · Yönetim ve yapılandırma merkezi</small>
      </section>
      <section className="login-form-wrap">
        <form className="login-form" onSubmit={submit}>
          <div className="login-form__icon"><LockKeyhole /></div>
          <span className="eyebrow">Yönetici girişi</span>
          <h2>Tekrar hoş geldiniz</h2>
          <p>Devam etmek için yönetici hesabınızla giriş yapın. Fiziksel PDA cihaz kodu gerekmez.</p>
          {error && <div className="login-error" role="alert">{error}</div>}
          <label><span>Kullanıcı adı</span><div className="input-with-icon"><KeyRound size={18} /><TextInput autoComplete="username" value={username} onChange={(event) => setUsername(event.target.value)} required /></div></label>
          <label><span>Parola</span><div className="input-with-icon"><LockKeyhole size={18} /><TextInput type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} required /></div></label>
          <Button type="submit" disabled={busy || !username || !password}>{busy ? 'Doğrulanıyor…' : 'Yönetim merkezine gir'} <ArrowRight size={18} /></Button>
          <div className="security-note"><ShieldCheck size={17} /> Oturum yalnızca uygulama belleğinde tutulur; sayfa yenilenince yeniden giriş gerekir.</div>
        </form>
      </section>
    </main>
  )
}
