import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router';
import { api, ok, TERMINAL } from '../api/client';
import { useFinalRefresh, useJobEvents, usePolling } from '../components/hooks';
import { ChartPanel, fetchOrders, LogsPanel, OrdersTable, pollWhile, StreamBadge } from '../components/ResultPanels';
import { Card, ErrorBox, StatGrid } from '../components/ui';
import { positionsFromOrders } from '../lean';
import { fetchResult, JobHeader, useLiveUpdates } from './JobDetail';
import { stopLive } from './Live';

export default function LiveDetail() {
  const { id = '' } = useParams();
  const [live, setLive] = useState(true);
  const events = useJobEvents(id, live);
  const streaming = events.state === 'live';
  const job = usePolling(async () => ok(await api.GET('/api/v1/jobs/{id}', { params: { path: { id } } })), [id, streaming], 3000, (j) => (!j || !TERMINAL.has(j.status)) && !streaming);
  useEffect(() => setLive(!job.data || !TERMINAL.has(job.data.status)), [job.data]);
  const final = useFinalRefresh(job.data?.status, !!job.data && TERMINAL.has(job.data.status));
  const result = usePolling(() => fetchResult(id), [id, streaming, final], 5000, () => pollWhile(live, events));
  const orders = usePolling(() => fetchOrders(id), [id, streaming, final], 5000, () => pollWhile(live, events));
  useLiveUpdates(events, job, result, orders);
  const positions = useMemo(() => positionsFromOrders(orders.data ?? []), [orders.data]);
  const [error, setError] = useState<Error>();

  // Keep the last job on screen while the gateway is unreachable; the stream and polling recover it.
  if (job.error && !job.data) return <ErrorBox error={job.error} />;
  if (!job.data) return <p className="muted">Loading…</p>;
  const spec = job.data.spec?.live;

  return (
    <div className="stack">
      <JobHeader
        job={job.data}
        actions={
          live && (
            <button
              className="primary"
              onClick={async () => {
                try {
                  await stopLive(id);
                } catch (e) {
                  setError(e as Error);
                }
                job.reload();
              }}
            >
              Stop deployment
            </button>
          )
        }
      />
      <StreamBadge events={events} />
      <ErrorBox error={job.error} />
      <ErrorBox error={error} />
      {(job.data.status === 'queued' || job.data.status === 'dispatched') && !result.data && (
        <p className="muted pending" data-testid="pending">
          {job.data.status === 'queued' ? 'Queued — waiting for a worker.' : 'Starting on a worker…'} Results appear here as soon as the job runs.
        </p>
      )}
      {spec && (
        <p className="muted small">
          Brokerage {spec.brokerage}
          {spec.dataQueueHandlers?.length ? ` · data ${spec.dataQueueHandlers.join(', ')}` : ''} · redeploy on stale {String(spec.redeployOnStale)}
        </p>
      )}
      <div className="cols">
        <Card title="Runtime">
          <StatGrid stats={result.data?.runtimeStatistics} />
        </Card>
        <Card title={`Positions (${positions.length})`}>
          <table className="table compact" data-testid="positions">
            <thead>
              <tr>
                <th>Symbol</th>
                <th className="num">Quantity</th>
                <th className="num">Avg price</th>
                <th className="num">Last fill</th>
                <th className="num">Market value</th>
              </tr>
            </thead>
            <tbody>
              {positions.map((p) => (
                <tr key={p.symbol}>
                  <td className="mono">{p.symbol}</td>
                  <td className={`num ${p.quantity < 0 ? 'neg' : ''}`}>{p.quantity}</td>
                  <td className="num">{p.averagePrice.toFixed(2)}</td>
                  <td className="num">{p.lastPrice.toFixed(2)}</td>
                  <td className="num">{(p.quantity * p.lastPrice).toFixed(2)}</td>
                </tr>
              ))}
              {!positions.length && (
                <tr>
                  <td colSpan={5} className="muted">
                    Flat — no open positions.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
          <p className="muted small">Net of filled orders from /api/v1/jobs/{'{id}'}/orders.</p>
        </Card>
      </div>
      <ChartPanel id={id} chart="Strategy Equity" title="Equity" live={live} events={events} refresh={final} />
      <OrdersTable orders={orders.data} error={orders.error} />
      <LogsPanel id={id} live={live} events={events} refresh={final} />
    </div>
  );
}
