import { useEffect, useMemo, useRef, useState } from 'react'
import { createHabitsApi, errorMessage, isValidToken, login, register } from './api.js'
import HabitTracker from './HabitTracker.jsx'

function AuthScreen({ onLogin, sessionNotice }) {
  const [registering, setRegistering] = useState(false)
  const [nickName, setNickName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)
  const submitting = useRef(false)

  async function submit(event) {
    event.preventDefault()
    if (submitting.current) return

    const address = email.trim()
    const name = nickName.trim()
    if (registering && !name) {
      setError('Введите никнейм.')
      return
    }
    submitting.current = true
    setPending(true)
    setError('')
    setNotice('')
    let accountCreated = false
    try {
      if (registering) {
        await register(name, address, password)
        accountCreated = true
      }
      onLogin(await login(address, password))
    } catch (requestError) {
      if (accountCreated) {
        setRegistering(false)
        setPassword('')
        setNotice('Аккаунт создан. Войдите с указанным email.')
      }
      setError(errorMessage(requestError))
    } finally {
      submitting.current = false
      setPending(false)
    }
  }

  function switchMode() {
    setRegistering(!registering)
    setNotice('')
    setError('')
  }

  return (
    <main className="auth-page">
      <section className="panel auth-panel">
        <p className="eyebrow">Трекер привычек</p>
        <h1>{registering ? 'Регистрация' : 'Вход'}</h1>
        <p className="muted">{registering ? 'Создайте аккаунт, чтобы начать.' : 'Отмечайте маленькие шаги каждый день.'}</p>
        {(notice || sessionNotice) && <p className="notice" role="status">{notice || sessionNotice}</p>}
        {error && <p className="error" role="alert">{error}</p>}
        <form className="form-stack" onSubmit={submit}>
          {registering && <label>Никнейм<input required autoComplete="nickname" value={nickName} onChange={(event) => setNickName(event.target.value)} disabled={pending} /></label>}
          <label>Email<input required type="email" autoComplete="email" value={email} onChange={(event) => setEmail(event.target.value)} disabled={pending} /></label>
          <label>Пароль<input required type="password" autoComplete={registering ? 'new-password' : 'current-password'} value={password} onChange={(event) => setPassword(event.target.value)} disabled={pending} /></label>
          <button className="primary" disabled={pending}>{pending ? 'Подождите…' : registering ? 'Зарегистрироваться' : 'Войти'}</button>
        </form>
        <button type="button" className="text-button auth-switch" onClick={switchMode} disabled={pending}>{registering ? 'Уже есть аккаунт' : 'Создать аккаунт'}</button>
      </section>
    </main>
  )
}

export default function App() {
  const [{ session, notice }, setAuth] = useState(() => {
    const token = localStorage.getItem('token')
    return { session: isValidToken(token) ? { token } : null, notice: '' }
  })

  useEffect(() => {
    if (session) localStorage.setItem('token', session.token)
    else localStorage.removeItem('token')
  }, [session])

  const api = useMemo(() => session && createHabitsApi(session.token, () => {
    setAuth((current) => current.session === session
      ? { session: null, notice: 'Сессия завершилась. Войдите снова.' }
      : current)
  }), [session])

  if (!session) return <AuthScreen onLogin={(token) => setAuth({ session: { token }, notice: '' })} sessionNotice={notice} />
  return <HabitTracker api={api} onLogout={() => setAuth({ session: null, notice: '' })} />
}
