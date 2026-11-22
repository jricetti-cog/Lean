import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { api, ok, reportDownloadUrl, resultDownloadUrl, TERMINAL, type Job, type ResultSummary } from '../api/client';
import { usePolling } from '../components/hooks';
import { ChartPanel, fetchOrders, LogsPanel, OrdersTable } from '../components/ResultPanels';
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
  const job = usePolling(async () => ok(await api.GET('/api/v1/jobs/{id}', { params: { path: { id } } })), [id], 2000, (j) => !j || !TERMINAL.has(j.status));
  const live = !job.data || !TERMINAL.has(job.data.status);
  const result = usePolling(() => fetchResult(id), [id], 3000, () => live);
  const orders = usePolling(() => fetchOrders(id), [id], 5000, () => live);

  if (job.error) return <ErrorBox error={job.error} />;
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
      <ChartPanel id={id} chart="Strategy Equity" title="Equity" live={live} />
      <ChartPanel id={id} chart="Drawdown" title="Drawdown" negative live={live} />
      <OrdersTable orders={orders.data} error={orders.error} />
      <LogsPanel id={id} live={live} />
    </div>
  );
}
