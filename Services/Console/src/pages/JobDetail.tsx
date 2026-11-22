import { useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import { api, ok, reportDownloadUrl, resultDownloadUrl, TERMINAL, type Job, type ResultSummary } from '../api/client';
import { useFinalRefresh, useJobEvent, useJobEvents, usePolling, type JobEvents, type Loadable } from '../components/hooks';
import { packetSummary, type OrderRow } from '../lean';
import { ChartPanel, fetchOrders, LogsPanel, OrdersTable, pollWhile, StreamBadge } from '../components/ResultPanels';
import { Card, duration, ErrorBox, fmtTime, KeyValues, Progress, StatGrid, StatusBadge } from '../components/ui';

export async function fetchResult(id: string): Promise<ResultSummary | undefined> {
  const r = await api.GET('/api/v1/jobs/{id}/result', { params: { path: { id } } });
  return r.response.status === 404 ? undefined : ok(r);
}

export function JobHeader({ job, actions }: { job: Job; actions?: React.ReactNode }) {
  return (
    <div className="job-head">
      <div>
        <h1>
          {job.name || job.kind} <StatusBadge status={job.status} />
        </h1>
        <p className="muted small mono">
          {job.kind} · {job.id} · attempt {job.attempt} · created {fmtTime(job.createdAt)} · ran {duration(job)}
          {job.parentJobId && (
            <>
              {' '}
              · parent <Link to={`/optimizations/${job.parentJobId}`}>{job.parentJobId.slice(0, 8)}</Link>
            </>
          )}
        </p>
        {job.error && (
          <div className="error">
            {job.error.code}: {job.error.message}
          </div>
        )}
      </div>
      <div className="actions">{actions}</div>
    </div>
  );
}

/**
 * Applies live events to the page's REST data: the snapshot and progress update the job, result packets the
 * runtime statistics, order events reload the orders (debounced) and the terminal status reloads everything.
 */
export function useLiveUpdates(events: JobEvents, job: Loadable<Job>, result: Loadable<ResultSummary | undefined>, orders: Loadable<OrderRow[]>) {
  const orderTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(orderTimer.current), []);
  // Live algorithms send `order` frames; backtests carry their new orders inside the result packets.
  const reloadOrders = () => {
    clearTimeout(orderTimer.current);
    orderTimer.current = setTimeout(orders.reload, 1000);
  };
  useJobEvent(events, ['snapshot', 'status', 'progress', 'result', 'order'], (e) => {
    const body = e.body as Record<string, unknown>;
    switch (e.event) {
      case 'snapshot':
        job.mutate(() => e.body as Job);
        break;
      case 'status':
        job.reload();
        result.reload();
        orders.reload();
        break;
      case 'progress': {
        const progress = typeof body.progress === 'number' ? body.progress : undefined;
        job.mutate((j) => (j && progress !== undefined ? { ...j, progress } : j));
        const runtimeStatistics = body.runtimeStatistics as Record<string, string> | undefined;
        if (runtimeStatistics) result.mutate((r) => (r ? { ...r, runtimeStatistics } : r));
        break;
      }
      case 'result': {
        const { progress, runtimeStatistics, newOrders } = packetSummary(e.body);
        if (newOrders) reloadOrders();
        if (!result.data) result.reload();
        else result.mutate((r) => r && { ...r, progress: progress ?? r.progress, runtimeStatistics: runtimeStatistics ?? r.runtimeStatistics });
        break;
      }
      case 'order':
        reloadOrders();
        break;
    }
  });
}

const reportKey = (id: string) => `lean-console:report:${id}`;

function ReportButton({ job }: { job: Job }) {
  const [reportId, setReportId] = useState(() => localStorage.getItem(reportKey(job.id)) ?? undefined);
  const [error, setError] = useState<Error>();
  const report = usePolling(
    async () => (reportId ? ok(await api.GET('/api/v1/reports/{id}', { params: { path: { id: reportId } } })) : undefined),
    [reportId],
    3000,
    (r) => !!reportId && (!r || !TERMINAL.has(r.status)),
  );
  const request = async () => {
    setError(undefined);
    try {
      const created = ok(await api.POST('/api/v1/reports', { body: { sourceJobId: job.id, strategyName: job.name, formats: ['html', 'pdf'] } }));
      localStorage.setItem(reportKey(job.id), created.id);
      setReportId(created.id);
    } catch (e) {
      setError(e as Error);
    }
  };
  if (report.data?.html) {
    return (
      <>
        <a className="button primary" href={reportDownloadUrl(reportId!, 'html')} target="_blank" rel="noreferrer">
          Open report
        </a>
        {report.data.pdf && (
          <a className="button ghost" href={reportDownloadUrl(reportId!, 'pdf')} target="_blank" rel="noreferrer">
            PDF
          </a>
        )}
      </>
    );
  }
  const pending = reportId && (!report.data || !TERMINAL.has(report.data.status));
  return (
    <>
      <button className="primary" disabled={job.status !== 'completed' || !!pending} onClick={request} title={error?.message}>
        {pending ? 'Rendering report…' : report.data?.status === 'failed' ? 'Retry report' : 'Generate report'}
      </button>
      {error && <span className="small neg">{error.message}</span>}
    </>
  );
}

export default function JobDetail() {
  const { id = '' } = useParams();
  const [live, setLive] = useState(true);
  const events = useJobEvents(id, live);
  const streaming = events.state === 'live';
  const job = usePolling(async () => ok(await api.GET('/api/v1/jobs/{id}', { params: { path: { id } } })), [id, streaming], 2000, (j) => (!j || !TERMINAL.has(j.status)) && !streaming);
  useEffect(() => setLive(!job.data || !TERMINAL.has(job.data.status)), [job.data]);
  const final = useFinalRefresh(job.data?.status, !!job.data && TERMINAL.has(job.data.status));
  const result = usePolling(() => fetchResult(id), [id, streaming, final], 3000, () => pollWhile(live, events));
  const orders = usePolling(() => fetchOrders(id), [id, streaming, final], 5000, () => pollWhile(live, events));
  useLiveUpdates(events, job, result, orders);

  // Keep the last job on screen while the gateway is unreachable; the stream and polling recover it.
  if (job.error && !job.data) return <ErrorBox error={job.error} />;
  if (!job.data) return <p className="muted">Loading…</p>;
  const j = job.data;
  const stats = result.data?.statistics;
  const runtime = result.data?.runtimeStatistics;

  return (
    <div className="stack">
      <JobHeader
        job={j}
        actions={
          <>
            {!TERMINAL.has(j.status) && (
              <button className="ghost" onClick={async () => (await api.POST('/api/v1/jobs/{id}/cancel', { params: { path: { id } } }), job.reload())}>
                Cancel
              </button>
            )}
            {result.data?.result && (
              <a className="button ghost" href={resultDownloadUrl(id)} target="_blank" rel="noreferrer">
                Result JSON
              </a>
            )}
            {j.kind === 'backtest' && <ReportButton job={j} />}
          </>
        }
      />
      <StreamBadge events={events} />
      <ErrorBox error={job.error} />
      {(j.status === 'queued' || j.status === 'dispatched') && !result.data && (
        <p className="muted pending" data-testid="pending">
          {j.status === 'queued' ? 'Queued — waiting for a worker.' : 'Starting on a worker…'} Results appear here as soon as the job runs.
        </p>
      )}
      {!TERMINAL.has(j.status) && <Progress value={result.data?.progress ?? j.progress} />}
      {j.spec?.backtest?.parameters && Object.keys(j.spec.backtest.parameters).length > 0 && (
        <p>
          Parameters: <KeyValues values={j.spec.backtest.parameters} />
        </p>
      )}
      <div className="cols">
        <Card title="Statistics">
          <StatGrid stats={stats} />
        </Card>
        <Card title="Runtime">
          <StatGrid stats={runtime} />
        </Card>
      </div>
      <ChartPanel id={id} chart="Strategy Equity" title="Equity" live={live} events={events} refresh={final} />
      <ChartPanel id={id} chart="Drawdown" title="Drawdown" negative live={live} events={events} refresh={final} />
      <OrdersTable orders={orders.data} error={orders.error} />
      <LogsPanel id={id} live={live} events={events} refresh={final} />
    </div>
  );
}
