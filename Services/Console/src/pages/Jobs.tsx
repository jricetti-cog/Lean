import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, type JobKind, type JobStatus, listJobs, TERMINAL } from '../api/client';
import { usePolling } from '../components/hooks';
import { Card, duration, ErrorBox, fmtTime, JobLink, Progress, StatusBadge } from '../components/ui';

const KINDS: JobKind[] = ['backtest', 'optimization', 'live', 'report', 'marketdata'];
const STATUSES: JobStatus[] = ['queued', 'dispatched', 'running', 'completed', 'failed', 'cancelled', 'stale'];

export default function Jobs() {
  const [search, setSearch] = useSearchParams();
  const kind = (search.get('kind') as JobKind) || undefined;
  const status = (search.get('status') as JobStatus) || undefined;
  const [cursors, setCursors] = useState<string[]>([]);
  const cursor = cursors.at(-1);
  const jobs = usePolling(
    () => listJobs({ kind, status, cursor, limit: 25 }),
    [kind, status, cursor],
  );
  const filter = (key: string, value: string) => {
    const next = new URLSearchParams(search);
    if (value) next.set(key, value);
    else next.delete(key);
    setCursors([]);
    setSearch(next);
  };

  const cancel = async (id: string) => {
    await api.POST('/api/v1/jobs/{id}/cancel', { params: { path: { id } } });
    jobs.reload();
  };

  return (
    <div className="stack">
      <h1>Jobs</h1>
      <Card
        actions={
          <>
            <select aria-label="Kind filter" value={kind ?? ''} onChange={(e) => filter('kind', e.target.value)}>
              <option value="">all kinds</option>
              {KINDS.map((k) => (
                <option key={k}>{k}</option>
              ))}
            </select>
            <select aria-label="Status filter" value={status ?? ''} onChange={(e) => filter('status', e.target.value)}>
              <option value="">all statuses</option>
              {STATUSES.map((s) => (
                <option key={s}>{s}</option>
              ))}
            </select>
          </>
        }
      >
        <ErrorBox error={jobs.error} />
        <table className="table">
          <thead>
            <tr>
              <th>Job</th>
              <th>Name</th>
              <th>Kind</th>
              <th>Status</th>
              <th>Progress</th>
              <th>Created</th>
              <th>Duration</th>
              <th>By</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {jobs.data?.items?.map((job) => (
              <tr key={job.id}>
                <td>
                  <JobLink job={job} />
                </td>
                <td>{job.name || '—'}</td>
                <td>{job.kind}</td>
                <td>
                  <StatusBadge status={job.status} />
                  {job.error && <div className="small neg">{job.error.code}</div>}
                </td>
                <td>
                  <Progress value={job.status === 'completed' ? 1 : job.progress} />
                </td>
                <td>{fmtTime(job.createdAt)}</td>
                <td>{duration(job)}</td>
                <td>{job.submittedBy}</td>
                <td>
                  {!TERMINAL.has(job.status) && (
                    <button className="ghost small" onClick={() => cancel(job.id)}>
                      Cancel
                    </button>
                  )}
                </td>
              </tr>
            ))}
            {jobs.data && !jobs.data.items?.length && (
              <tr>
                <td colSpan={9} className="muted">
                  No jobs.
                </td>
              </tr>
            )}
          </tbody>
        </table>
        <div className="row end">
          <button className="ghost" disabled={!cursors.length} onClick={() => setCursors((c) => c.slice(0, -1))}>
            ← Newer
          </button>
          <button className="ghost" disabled={!jobs.data?.next} onClick={() => setCursors((c) => [...c, jobs.data!.next!])}>
            Older →
          </button>
        </div>
      </Card>
    </div>
  );
}
