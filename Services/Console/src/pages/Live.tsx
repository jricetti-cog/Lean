import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { api, listJobs, ok, TERMINAL } from '../api/client';
import { AlgorithmPicker, resolveAlgorithm, type AlgorithmChoice } from '../components/AlgorithmPicker';
import { usePolling } from '../components/hooks';
import { ParamsEditor, toParameters, type ParamRow } from '../components/ParamsEditor';
import { Card, duration, ErrorBox, fmtTime, JobLink, StatusBadge } from '../components/ui';

export const stopLive = async (id: string) => ok(await api.POST('/api/v1/live/{id}/stop', { params: { path: { id } } }));

export default function Live() {
  const navigate = useNavigate();
  const deployments = usePolling(() => listJobs({ kind: 'live', limit: 50 }), []);
  const [algorithm, setAlgorithm] = useState<AlgorithmChoice>();
  const [name, setName] = useState('');
  const [brokerage, setBrokerage] = useState('PaperBrokerage');
  const [handlers, setHandlers] = useState('');
  const [redeploy, setRedeploy] = useState(true);
  const [params, setParams] = useState<ParamRow[]>([]);
  const [error, setError] = useState<Error>();
  const [busy, setBusy] = useState(false);

  const deploy = async (e: FormEvent) => {
    e.preventDefault();
    if (!algorithm) return;
    setBusy(true);
    setError(undefined);
    try {
      const job = ok(
        await api.POST('/api/v1/live', {
          body: {
            name: name || algorithm.sample?.name,
            algorithm: await resolveAlgorithm(algorithm),
            parameters: toParameters(params),
            brokerage,
            dataQueueHandlers: handlers ? handlers.split(',').map((h) => h.trim()).filter(Boolean) : undefined,
            redeployOnStale: redeploy,
          },
        }),
      );
      navigate(`/live/${job.id}`);
    } catch (err) {
      setError(err as Error);
    } finally {
      setBusy(false);
    }
  };

  const stop = async (id: string) => {
    try {
      await stopLive(id);
    } catch (err) {
      setError(err as Error);
    }
    deployments.reload();
  };

  return (
    <div className="stack">
      <h1>Live deployments</h1>
      <Card title="Deployments">
        <table className="table">
          <thead>
            <tr>
              <th>Deployment</th>
              <th>Name</th>
              <th>Status</th>
              <th>Brokerage</th>
              <th>Started</th>
              <th>Uptime</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {deployments.data?.items?.map((j) => (
              <tr key={j.id}>
                <td>
                  <JobLink job={j} />
                </td>
                <td>{j.name || '—'}</td>
                <td>
                  <StatusBadge status={j.status} />
                </td>
                <td>{j.spec?.live?.brokerage ?? '—'}</td>
                <td>{fmtTime(j.startedAt ?? j.createdAt)}</td>
                <td>{duration(j)}</td>
                <td>
                  {!TERMINAL.has(j.status) && (
                    <button className="ghost small" onClick={() => stop(j.id)}>
                      Stop
                    </button>
                  )}
                </td>
              </tr>
            ))}
            {deployments.data && !deployments.data.items?.length && (
              <tr>
                <td colSpan={7} className="muted">
                  No live deployments.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </Card>
      <form onSubmit={deploy} className="stack">
        <h2>Start a deployment</h2>
        <Card title="Algorithm">
          <AlgorithmPicker value={algorithm} onChange={setAlgorithm} height={300} />
        </Card>
        <Card title="Deployment">
          <div className="grid-form">
            <label>
              Name <input value={name} onChange={(e) => setName(e.target.value)} placeholder="optional" />
            </label>
            <label>
              Brokerage <input value={brokerage} onChange={(e) => setBrokerage(e.target.value)} required />
            </label>
            <label>
              Data queue handlers <input value={handlers} onChange={(e) => setHandlers(e.target.value)} placeholder="e.g. bus (comma separated)" />
            </label>
            <label className="check">
              <input type="checkbox" checked={redeploy} onChange={(e) => setRedeploy(e.target.checked)} /> Redeploy when stale
            </label>
          </div>
          <h3>Parameters</h3>
          <ParamsEditor rows={params} onChange={setParams} />
        </Card>
        <ErrorBox error={error} />
        <div className="row end">
          <button type="submit" className="primary" disabled={busy || !algorithm?.source.trim()}>
            {busy ? 'Deploying…' : 'Start deployment'}
          </button>
        </div>
      </form>
    </div>
  );
}
