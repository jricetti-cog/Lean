import { api } from '../api/client';
import { usePolling } from './hooks';

/** Probes the gateway through the console proxy (an authenticated list call proves both reachability and the key). */
export function GatewayHealth() {
  const probe = usePolling(async () => {
    const r = await api.GET('/api/v1/jobs', { params: { query: { limit: 1 } } });
    return r.response.status;
  }, [], 15000);
  const status = probe.error ? 'unreachable' : probe.data === 200 ? 'connected' : probe.data === 401 ? 'bad API key' : probe.data ? `HTTP ${probe.data}` : '…';
  return (
    <div className={`health ${probe.data === 200 ? 'up' : 'down'}`} title="api-gateway via /api">
      <span className="dot" /> gateway {status}
    </div>
  );
}
