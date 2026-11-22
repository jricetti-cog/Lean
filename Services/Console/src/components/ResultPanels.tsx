import { useMemo, useState } from 'react';
import { allPages, api, ok, type LeanObject, type LogLine } from '../api/client';
import { chartSeries, mergeChart, orderRow, packetChart, packetLogLine, type OrderRow } from '../lean';
import { useJobEvent, usePolling, type JobEvents } from './hooks';
import { Card, ErrorBox, fmtTime } from './ui';
import { TimeChart } from './TimeChart';

export async function fetchOrders(id: string): Promise<OrderRow[]> {
  const items = await allPages<LeanObject>(async (cursor) =>
    pending(await api.GET('/api/v1/jobs/{id}/orders', { params: { path: { id }, query: { cursor, limit: 500 } } })),
  );
  return items.map(orderRow);
}

/** A job without results yet (queued, or its first packet not stored) has no pages rather than an error. */
const pending = <T,>(r: { data?: { items?: T[]; next?: string | null }; error?: unknown; response: Response }) => (r.response.status === 404 ? {} : ok(r));

async function fetchChart(id: string, chart: string) {
  const r = await api.GET('/api/v1/jobs/{id}/charts/{chart}', { params: { path: { id, chart } } });
  return r.response.status === 404 ? undefined : ok(r);
}

/** Polls only while the event stream is not delivering (initial load, reconnects, gateway without a bus). */
export const pollWhile = (live: boolean, events?: JobEvents) => live && events?.state !== 'live';

export function ChartPanel({ id, chart, title, negative, live, events, refresh }: { id: string; chart: string; title: string; negative?: boolean; live: boolean; events?: JobEvents; refresh?: number }) {
  const streaming = events?.state === 'live';
  const data = usePolling(() => fetchChart(id, chart), [id, chart, streaming, refresh], 5000, () => pollWhile(live, events));
  // Partial result packets carry only the new points: append them, or reload when the gateway dropped some
  // (`dropped`, latest-wins backpressure) or the packet went by reference.
  useJobEvent(events, ['result', 'status'], (e) => {
    const delta = e.event === 'result' ? packetChart(e.body, chart) : undefined;
    if (e.event === 'status' || e.dropped || e.type === 'packets.v1.PacketRef') data.reload();
    else if (delta) data.mutate((base) => mergeChart(base, delta));
  });
  const series = useMemo(() => chartSeries(data.data), [data.data]);
  return (
    <Card title={title}>
      <ErrorBox error={data.error} />
      {series.length ? <TimeChart series={negative ? series.slice(0, 1) : series.slice(0, 3)} negative={negative} /> : <p className="muted">{data.loading ? 'Loading…' : 'No data yet.'}</p>}
    </Card>
  );
}

export function OrdersTable({ orders, error }: { orders?: OrderRow[]; error?: Error }) {
  const [page, setPage] = useState(0);
  const size = 50;
  const rows = orders ?? [];
  const shown = rows.slice(page * size, page * size + size);
  return (
    <Card title={`Orders (${rows.length})`}>
      <ErrorBox error={error} />
      <table className="table compact">
        <thead>
          <tr>
            <th>#</th>
            <th>Time</th>
            <th>Symbol</th>
            <th>Type</th>
            <th>Status</th>
            <th className="num">Quantity</th>
            <th className="num">Price</th>
            <th className="num">Value</th>
            <th>Tag</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((o) => (
            <tr key={o.id}>
              <td>{o.id}</td>
              <td>{fmtTime(o.time)}</td>
              <td className="mono">{o.symbol}</td>
              <td>{o.type}</td>
              <td>{o.status}</td>
              <td className={`num ${o.quantity < 0 ? 'neg' : ''}`}>{o.quantity}</td>
              <td className="num">{o.price.toFixed(2)}</td>
              <td className="num">{o.value.toFixed(2)}</td>
              <td className="small">{o.tag ?? ''}</td>
            </tr>
          ))}
          {!rows.length && (
            <tr>
              <td colSpan={9} className="muted">
                No orders.
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {rows.length > size && (
        <div className="row end">
          <button className="ghost" disabled={page === 0} onClick={() => setPage(page - 1)}>
            ←
          </button>
          <span className="small">
            {page + 1} / {Math.ceil(rows.length / size)}
          </span>
          <button className="ghost" disabled={(page + 1) * size >= rows.length} onClick={() => setPage(page + 1)}>
            →
          </button>
        </div>
      )}
    </Card>
  );
}

export function LogsPanel({ id, live, events, refresh }: { id: string; live: boolean; events?: JobEvents; refresh?: number }) {
  const streaming = events?.state === 'live';
  const logs = usePolling(
    async () => allPages<LogLine>(async (cursor) => pending(await api.GET('/api/v1/jobs/{id}/logs', { params: { path: { id }, query: { cursor, limit: 1000 } } }))),
    [id, streaming, refresh],
    5000,
    () => pollWhile(live, events),
  );
  useJobEvent(events, ['log', 'error', 'status'], (e) => {
    const line = e.event === 'status' ? undefined : packetLogLine(e.type, e.body);
    if (!line || e.dropped) logs.reload();
    else logs.mutate((lines) => [...(lines ?? []), line as LogLine]);
  });
  return (
    <Card title={`Logs (${logs.data?.length ?? 0})`}>
      <ErrorBox error={logs.error} />
      <pre className="logs" data-testid="logs">
        {logs.data?.length
          ? logs.data.map((l) => `${l.time} ${l.type === 'Log' ? '' : `[${l.type}] `}${l.message}${l.stackTrace ? `\n${l.stackTrace}` : ''}`).join('\n')
          : 'No log lines yet.'}
      </pre>
    </Card>
  );
}

const STREAM_LABEL = { live: 'live', connecting: 'connecting…', polling: 'polling', closed: '' } as const;

/** Whether the page is fed by the SSE stream or has fallen back to polling. */
export function StreamBadge({ events }: { events: JobEvents }) {
  if (events.state === 'closed') return null;
  return (
    <span className={`stream stream-${events.state}`} data-testid="stream-state" title="GET /api/v1/jobs/{id}/events">
      ● {STREAM_LABEL[events.state]}
    </span>
  );
}
