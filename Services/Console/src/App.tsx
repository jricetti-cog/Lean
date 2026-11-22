import { NavLink, Navigate, Route, Routes } from 'react-router';
import SubmitBacktest from './pages/SubmitBacktest';
import Jobs from './pages/Jobs';
import JobDetail from './pages/JobDetail';
import Optimizations from './pages/Optimizations';
import OptimizationDetail from './pages/OptimizationDetail';
import Live from './pages/Live';
import LiveDetail from './pages/LiveDetail';
import { GatewayHealth } from './components/GatewayHealth';

const nav = [
  ['/backtests/new', 'New backtest'],
  ['/jobs', 'Jobs'],
  ['/optimizations', 'Optimizations'],
  ['/live', 'Live'],
] as const;

export default function App() {
  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          <span className="logo">L</span>
          <span>LEAN Console</span>
        </div>
        <nav>
          {nav.map(([to, label]) => (
            <NavLink key={to} to={to} className={({ isActive }) => (isActive ? 'active' : '')}>
              {label}
            </NavLink>
          ))}
        </nav>
        <GatewayHealth />
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Navigate to="/jobs" replace />} />
          <Route path="/backtests/new" element={<SubmitBacktest />} />
          <Route path="/jobs" element={<Jobs />} />
          <Route path="/jobs/:id" element={<JobDetail />} />
          <Route path="/optimizations" element={<Optimizations />} />
          <Route path="/optimizations/:id" element={<OptimizationDetail />} />
          <Route path="/live" element={<Live />} />
          <Route path="/live/:id" element={<LiveDetail />} />
          <Route path="*" element={<p className="muted">Page not found.</p>} />
        </Routes>
      </main>
    </div>
  );
}
