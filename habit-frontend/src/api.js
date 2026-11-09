import axios from 'axios';

const authApi = axios.create({
  baseURL: 'http://localhost:8081/api/auth',
});

const habitsApi = axios.create({
  baseURL: 'http://localhost:8082/api/habits',
});

habitsApi.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

habitsApi.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('token');
      window.location.reload(); 
    }
    return Promise.reject(error);
  }
);


export const login = (email, password) => 
  authApi.post('/login', { email, password }).then(res => res.data.token);

export const register = (email, password) => 
  authApi.post('/register', { email, password }).then(res => res.data.token);

export const getHabits = () => 
  habitsApi.get('').then(res => res.data);

export const createHabit = (name) => 
  habitsApi.post('', { name }).then(res => res.data);

export const deleteHabit = (id) => 
  habitsApi.delete(`/${id}`);

export const checkinHabit = (id) => 
  habitsApi.post(`/${id}/checkins`, {}).then(res => res.data);

export const getStats = (id) => 
  habitsApi.get(`/${id}/stats`).then(res => res.data);