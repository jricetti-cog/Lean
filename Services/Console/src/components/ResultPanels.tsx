import { useMemo, useState } from 'react';
import { allPages, api, ok, type LeanObject, type LogLine } from '../api/client';
import { chartSeries, orderRow, type OrderRow } from '../lean';
import { usePolling } from './hooks';
import { Card, ErrorBox, fmtTime } from './ui';
import { TimeChart } from './TimeChart';

export async function fetchOrders(id: string): Promise<OrderRow[]> {
  const items = await allPages<LeanObject>(async (cursor) =>
    ok(await api.GET('/api/v1/jobs/{id}/orders', { params: { path: { id }, query: { cursor, limit: 500 } } })),
  );
  return items.map(orderRow);
}

async function fetchChart(id: string, chart: string) {
  const r = await api.GET('/api/v1/jobs/{id}/charts/{chart}', { params: { path: { id, chart } } });
  return r.response.status === 404 ? undefined : ok(r);
}

export function ChartPanel({ id, chart, title, negative, live }: { id: string; chart: string; title: string; negative?: boolean; live: boolean }) {
  const data = usePolling(() => fetchChart(id, chart), [id, chart], 5000, () => live);
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

export function LogsPanel({ id, live }: { id: string; live: boolean }) {
  const logs = usePolling(
    async () => allPages<LogLine>(async (cursor) => ok(await api.GET('/api/v1/jobs/{id}/logs', { params: { path: { id }, query: { cursor, limit: 1000 } } }))),
    [id],
    5000,
    () => live,
  );
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
