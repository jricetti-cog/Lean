import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router';
import { allPages, api, type Job, listJobs, ok, type ResultSummary, TERMINAL } from '../api/client';
import { usePolling } from '../components/hooks';
import { Card, ErrorBox, Progress, StatusBadge } from '../components/ui';
import { fetchResult, JobHeader } from './JobDetail';

const STAT_COLUMNS = ['Sharpe Ratio', 'Net Profit', 'Compounding Annual Return', 'Drawdown', 'Total Orders'];

type Row = { job: Job; result?: ResultSummary };

const numeric = (v?: string) => (v === undefined ? NaN : Number(v.replace(/[%$,]/g, '')));

export default function OptimizationDetail() {
  const { id = '' } = useParams();
  const job = usePolling(async () => ok(await api.GET('/api/v1/jobs/{id}', { params: { path: { id } } })), [id], 3000, (j) => !j || !TERMINAL.has(j.status));
  const live = !job.data || !TERMINAL.has(job.data.status);
  const state = usePolling(
    async () => {
      const r = await api.GET('/api/v1/optimizations/{id}', { params: { path: { id } } });
      return r.response.status === 404 ? undefined : ok(r);
    },
    [id],
    3000,
    () => live,
  );
  const rows = usePolling<Row[]>(
    async () => {
      const children = await allPages<Job>((cursor) => listJobs({ parentJobId: id, cursor, limit: 100 }), 5);
      return Promise.all(children.map(async (c) => ({ job: c, result: c.status === 'completed' ? await fetchResult(c.id).catch(() => undefined) : undefined })));
    },
    [id],
    5000,
    () => live,
  );
  const [sort, setSort] = useState<string>('Sharpe Ratio');
  const [desc, setDesc] = useState(true);

  const paramNames = useMemo(() => [...new Set((rows.data ?? []).flatMap((r) => Object.keys(r.job.spec?.backtest?.parameters ?? {})))].sort(), [rows.data]);
  const value = (r: Row, col: string) => (paramNames.includes(col) ? r.job.spec?.backtest?.parameters?.[col] : r.result?.statistics?.[col]);
  const sorted = useMemo(() => {
    const list = [...(rows.data ?? [])];
    list.sort((a, b) => {
      const x = numeric(value(a, sort));
      const y = numeric(value(b, sort));
      if (Number.isNaN(x)) return 1;
      if (Number.isNaN(y)) return -1;
      return desc ? y - x : x - y;
    });
    return list;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows.data, sort, desc, paramNames]);
  const range = useMemo(() => {
    const vals = sorted.map((r) => numeric(value(r, sort))).filter((v) => !Number.isNaN(v));
    return { min: Math.min(...vals), max: Math.max(...vals) };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sorted, sort]);

  if (job.error) return <ErrorBox error={job.error} />;
  if (!job.data) return <p className="muted">Loading…</p>;
  const s = state.data;
  const best = s?.best?.jobId;
  const header = (col: string) => (
    <th key={col} className={`sortable ${sort === col ? 'sorted' : ''}`} onClick={() => (sort === col ? setDesc(!desc) : (setSort(col), setDesc(true)))}>
      {col} {sort === col ? (desc ? '▼' : '▲') : ''}
    </th>
  );
  const shade = (r: Row) => {
    const v = numeric(value(r, sort));
    if (Number.isNaN(v) || range.max === range.min) return undefined;
    return { background: `rgba(37, 99, 235, ${(0.06 + 0.3 * ((v - range.min) / (range.max - range.min))).toFixed(2)})` };
  };

  return (
    <div className="stack">
      <JobHeader
        job={job.data}
        actions={
          live && (
            <button className="ghost" onClick={async () => (await api.POST('/api/v1/jobs/{id}/cancel', { params: { path: { id } } }), job.reload())}>
              Cancel
            </button>
          )
        }
      />
      <Card title="Progress">
        <ErrorBox error={state.error} />
        {s ? (
          <>
            <Progress value={s.totalBacktests ? (s.completed + s.failed) / s.totalBacktests : 0} />
            <p className="small">
              {s.completed} completed · {s.running} running · {s.queued} queued · {s.failed} failed of {s.totalBacktests} backtests
              {s.best && (
                <>
                  {' '}
                  · best <Link to={`/jobs/${s.best.jobId}`}>{s.best.jobId.slice(0, 8)}</Link> objective {s.best.objective ?? '—'}
                </>
              )}
            </p>
          </>
        ) : (
          <p className="muted">Waiting for the optimizer…</p>
        )}
      </Card>
      <Card title={`Results grid (${sorted.length})`}>
        <ErrorBox error={rows.error} />
        <div className="scroll-x">
          <table className="table compact" data-testid="optimization-grid">
            <thead>
              <tr>
                <th>Backtest</th>
                <th>Status</th>
                {paramNames.map(header)}
                {STAT_COLUMNS.map(header)}
              </tr>
            </thead>
            <tbody>
              {sorted.map((r) => (
                <tr key={r.job.id} className={r.job.id === best ? 'best' : ''}>
                  <td>
                    <Link to={`/jobs/${r.job.id}`} className="mono">
                      {r.job.id.slice(0, 8)}
                    </Link>
                    {r.job.id === best && <span className="badge badge-best">best</span>}
                  </td>
                  <td>
                    <StatusBadge status={r.job.status} />
                  </td>
                  {paramNames.map((p) => (
                    <td key={p} className="num" style={sort === p ? shade(r) : undefined}>
                      {r.job.spec?.backtest?.parameters?.[p] ?? '—'}
                    </td>
                  ))}
                  {STAT_COLUMNS.map((c) => (
                    <td key={c} className="num" style={sort === c ? shade(r) : undefined}>
                      {r.result?.statistics?.[c] ?? '—'}
                    </td>
                  ))}
                </tr>
              ))}
              {!sorted.length && (
                <tr>
                  <td colSpan={2 + paramNames.length + STAT_COLUMNS.length} className="muted">
                    No child backtests yet.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}
