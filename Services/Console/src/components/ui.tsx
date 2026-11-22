import type { ReactNode } from 'react';
import { Link } from 'react-router';
import type { Job, JobStatus } from '../api/client';

export function StatusBadge({ status }: { status: JobStatus | string }) {
  return <span className={`badge badge-${status}`}>{status}</span>;
}

export function Progress({ value }: { value?: number }) {
  const pct = Math.round(Math.max(0, Math.min(1, value ?? 0)) * 100);
  return (
    <div className="progress" title={`${pct}%`}>
      <div style={{ width: `${pct}%` }} />
    </div>
  );
}

export function Card({ title, actions, children }: { title?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return (
    <section className="card">
      {(title || actions) && (
        <div className="card-head">
          <h2>{title}</h2>
          <div className="actions">{actions}</div>
        </div>
      )}
      {children}
    </section>
  );
}

export function ErrorBox({ error }: { error?: Error | null }) {
  return error ? <div className="error" role="alert">{error.message}</div> : null;
}

export function JobLink({ job }: { job: Pick<Job, 'id' | 'kind'> }) {
  const base = job.kind === 'optimization' ? '/optimizations' : job.kind === 'live' ? '/live' : '/jobs';
  return (
    <Link to={`${base}/${job.id}`} className="mono">
      {job.id.slice(0, 8)}
    </Link>
  );
}

export const fmtTime = (value?: string | null) => (value ? new Date(value).toLocaleString() : '—');

export function duration(job: Pick<Job, 'startedAt' | 'finishedAt'>) {
  if (!job.startedAt) return '—';
  const ms = (job.finishedAt ? Date.parse(job.finishedAt) : Date.now()) - Date.parse(job.startedAt);
  const s = Math.max(0, Math.round(ms / 1000));
  return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`;
}

export function StatGrid({ stats }: { stats?: Record<string, string> }) {
  const entries = Object.entries(stats ?? {});
  if (!entries.length) return <p className="muted">No statistics yet.</p>;
  return (
    <dl className="stats">
      {entries.map(([k, v]) => (
        <div key={k}>
          <dt>{k}</dt>
          <dd className={v.startsWith('-') ? 'neg' : ''}>{v}</dd>
        </div>
      ))}
    </dl>
  );
}

export function KeyValues({ values }: { values?: Record<string, string> | null }) {
  const entries = Object.entries(values ?? {});
  return entries.length ? <span className="mono small">{entries.map(([k, v]) => `${k}=${v}`).join(', ')}</span> : <span className="muted">—</span>;
}
