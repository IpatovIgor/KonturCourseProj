import { useState, useEffect } from 'react';
import { 
  login, register, getHabits, createHabit, 
  deleteHabit, checkinHabit, getStats 
} from './api';

function AuthScreen({ onLogin }) {
  const [isLogin, setIsLogin] = useState(true);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError('');
    try {
      const token = isLogin 
        ? await login(email, password) 
        : await register(email, password);
      localStorage.setItem('token', token);
      onLogin(); 
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.title || 'Ошибка входа');
    }
  };

  return (
    <div style={{ maxWidth: 350, margin: '100px auto', fontFamily: 'sans-serif' }}>
      <h2>{isLogin ? 'Вход' : 'Регистрация'}</h2>
      {error && <p style={{ color: 'red', fontSize: 14 }}>{error}</p>}
      
      <form onSubmit={handleSubmit} style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <input 
          type="email" 
          placeholder="Email" 
          value={email} 
          onChange={e => setEmail(e.target.value)} 
          required 
          style={{ padding: 8 }}
        />
        <input 
          type="password" 
          placeholder="Пароль (мин. 6 символов)" 
          value={password} 
          onChange={e => setPassword(e.target.value)} 
          required 
          style={{ padding: 8 }}
        />
        <button type="submit" style={{ padding: 10, cursor: 'pointer', background: '#007bff', color: 'white', border: 'none' }}>
          {isLogin ? 'Войти' : 'Создать аккаунт'}
        </button>
      </form>

      <button 
        onClick={() => setIsLogin(!isLogin)} 
        style={{ marginTop: 15, background: 'none', border: 'none', color: '#007bff', cursor: 'pointer', textDecoration: 'underline' }}
      >
        {isLogin ? 'Нет аккаунта? Зарегистрироваться' : 'Уже есть аккаунт? Войти'}
      </button>
    </div>
  );
}

export default function App() {
  const [isAuth, setIsAuth] = useState(!!localStorage.getItem('token'));
  const [habits, setHabits] = useState([]);
  const [stats, setStats] = useState({});
  const [newHabit, setNewHabit] = useState('');
  const [loading, setLoading] = useState(false);

  const loadData = async () => {
    setLoading(true);
    try {
      const list = await getHabits();
      setHabits(list);
      
      const statsMap = {};
      for (let h of list) {
        statsMap[h.id] = await getStats(h.id);
      }
      setStats(statsMap);
    } catch (e) {
      console.error(e);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (isAuth) loadData();
  }, [isAuth]);

  const handleLogout = () => {
    localStorage.removeItem('token');
    setIsAuth(false);
  };

  const handleAdd = async (e) => {
    e.preventDefault();
    if (!newHabit.trim()) return;
    try {
      await createHabit(newHabit);
      setNewHabit('');
      loadData(); 
    } catch (e) {
      alert('Ошибка создания');
    }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Удалить привычку?')) return;
    await deleteHabit(id);
    loadData();
  };

  const handleCheckin = async (id) => {
    try {
      await checkinHabit(id);
      loadData(); 
    } catch (e) {
      alert(e.response?.data?.error || 'Уже отмечено сегодня');
    }
  };

  if (!isAuth) {
    return <AuthScreen onLogin={() => setIsAuth(true)} />;
  }

  return (
    <div style={{ maxWidth: 600, margin: '40px auto', fontFamily: 'sans-serif' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h2>Мои привычки</h2>
        <button onClick={handleLogout} style={{ cursor: 'pointer', padding: 5 }}>Выйти</button>
      </div>

      {/* Форма добавления */}
      <form onSubmit={handleAdd} style={{ display: 'flex', gap: 10, marginBottom: 20 }}>
        <input 
          placeholder="Новая привычка (например, Пить воду)..." 
          value={newHabit} 
          onChange={e => setNewHabit(e.target.value)}
          style={{ flex: 1, padding: 8 }}
        />
        <button type="submit" style={{ padding: '8px 15px', background: 'green', color: 'white', border: 'none', cursor: 'pointer' }}>
          Добавить
        </button>
      </form>

      {loading && <p>Загрузка...</p>}
      {!loading && habits.length === 0 && <p>Пока нет привычек. Добавь первую!</p>}

      {/* Список привычек */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 15 }}>
        {habits.map(h => {
          const s = stats[h.id] || { streak: 0, completionRate: 0 };
          return (
            <div key={h.id} style={{ border: '1px solid #ddd', borderRadius: 8, padding: 15 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                <h3 style={{ margin: 0 }}>{h.name}</h3>
                <button onClick={() => handleDelete(h.id)} style={{ background: 'none', border: 'none', color: 'red', cursor: 'pointer' }}>✕</button>
              </div>
              
              <div style={{ margin: '10px 0', fontSize: 14, color: '#555' }}>
                🔥 Серия: <b>{s.streak}</b> дн. | 📊 Выполнение: <b>{s.completionRate}%</b>
              </div>

              <button 
                onClick={() => handleCheckin(h.id)}
                style={{ padding: '6px 12px', background: '#f0f0f0', border: '1px solid #ccc', borderRadius: 4, cursor: 'pointer' }}
              >
                Отметить на сегодня
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}