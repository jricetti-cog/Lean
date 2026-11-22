import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { api, listJobs, ok, type OptimizationSpec } from '../api/client';
import { AlgorithmPicker, resolveAlgorithm, type AlgorithmChoice } from '../components/AlgorithmPicker';
import { usePolling } from '../components/hooks';
import { Card, ErrorBox, fmtTime, JobLink, Progress, StatusBadge } from '../components/ui';
import { BacktestFieldsForm, backtestSpec, emptyFields } from './SubmitBacktest';

type Range = { name: string; min: string; max: string; step: string };

const STRATEGIES = ['QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy', 'QuantConnect.Optimizer.Strategies.EulerSearchOptimizationStrategy'];
const TARGETS = [
  'TotalPerformance.PortfolioStatistics.SharpeRatio',
  'TotalPerformance.PortfolioStatistics.CompoundingAnnualReturn',
  'TotalPerformance.PortfolioStatistics.Drawdown',
  'TotalPerformance.PortfolioStatistics.TotalNetProfit',
  'TotalPerformance.PortfolioStatistics.ProbabilisticSharpeRatio',
];

function OptimizationForm() {
  const navigate = useNavigate();
  const [algorithm, setAlgorithm] = useState<AlgorithmChoice>();
  const [fields, setFields] = useState(emptyFields);
  const [ranges, setRanges] = useState<Range[]>([{ name: 'ema-fast', min: '10', max: '50', step: '10' }]);
  const [strategy, setStrategy] = useState(STRATEGIES[0]);
  const [target, setTarget] = useState(TARGETS[0]);
  const [extremum, setExtremum] = useState<'max' | 'min'>('max');
  const [concurrency, setConcurrency] = useState('4');
  const [estimate, setEstimate] = useState<number>();
  const [error, setError] = useState<Error>();
  const [busy, setBusy] = useState(false);

  const spec = async (): Promise<OptimizationSpec> => ({
    name: fields.name || (algorithm?.sample ? `${algorithm.sample.name} optimization` : undefined),
    backtest: backtestSpec(fields, await resolveAlgorithm(algorithm!), []),
    strategy,
    criterion: { target, extremum },
    parameters: ranges.filter((r) => r.name).map((r) => ({ name: r.name, min: Number(r.min), max: Number(r.max), step: Number(r.step) })),
    maximumConcurrentBacktests: Number(concurrency || 4),
  });

  const run = async (action: 'estimate' | 'submit') => {
    setBusy(true);
    setError(undefined);
    try {
      const body = await spec();
      if (action === 'estimate') setEstimate(ok(await api.POST('/api/v1/optimizations/estimate', { body })).totalBacktests);
      else navigate(`/optimizations/${ok(await api.POST('/api/v1/optimizations', { body })).id}`);
    } catch (e) {
      setError(e as Error);
    } finally {
      setBusy(false);
    }
  };

  const setRange = (i: number, patch: Partial<Range>) => setRanges(ranges.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const submit = (e: FormEvent) => (e.preventDefault(), run('submit'));

  return (
    <form onSubmit={submit} className="stack">
      <Card title="Template algorithm">
        <AlgorithmPicker value={algorithm} onChange={setAlgorithm} height={300} />
        <BacktestFieldsForm fields={fields} onChange={setFields} />
      </Card>
      <Card title="Parameters to optimize">
        <table className="table compact">
          <thead>
            <tr>
              <th>Name</th>
              <th>Min</th>
              <th>Max</th>
              <th>Step</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {ranges.map((r, i) => (
              <tr key={i}>
                {(['name', 'min', 'max', 'step'] as const).map((k) => (
                  <td key={k}>
                    <input aria-label={`Parameter ${k}`} type={k === 'name' ? 'text' : 'number'} value={r[k]} onChange={(e) => setRange(i, { [k]: e.target.value })} />
                  </td>
                ))}
                <td>
                  <button type="button" className="ghost" onClick={() => setRanges(ranges.filter((_, j) => j !== i))} aria-label="Remove range">
                    ×
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <button type="button" className="ghost" onClick={() => setRanges([...ranges, { name: '', min: '0', max: '1', step: '1' }])}>
          + parameter
        </button>
        <div className="grid-form">
          <label>
            Strategy
            <select value={strategy} onChange={(e) => setStrategy(e.target.value)}>
              {STRATEGIES.map((s) => (
                <option key={s} value={s}>
                  {s.split('.').pop()}
                </option>
              ))}
            </select>
          </label>
          <label>
            Target
            <select value={target} onChange={(e) => setTarget(e.target.value)}>
              {TARGETS.map((t) => (
                <option key={t} value={t}>
                  {t.split('.').pop()}
                </option>
              ))}
            </select>
          </label>
          <label>
            Extremum
            <select value={extremum} onChange={(e) => setExtremum(e.target.value as 'max' | 'min')}>
              <option value="max">maximize</option>
              <option value="min">minimize</option>
            </select>
          </label>
          <label>
            Concurrent backtests <input type="number" min="1" value={concurrency} onChange={(e) => setConcurrency(e.target.value)} />
          </label>
        </div>
      </Card>
      <ErrorBox error={error} />
      <div className="row end">
        {estimate !== undefined && <span className="muted">{estimate} backtests</span>}
        <button type="button" className="ghost" disabled={busy || !algorithm} onClick={() => run('estimate')}>
          Estimate
        </button>
        <button type="submit" className="primary" disabled={busy || !algorithm}>
          {busy ? 'Submitting…' : 'Start optimization'}
        </button>
      </div>
    </form>
  );
}

export default function Optimizations() {
  const jobs = usePolling(() => listJobs({ kind: 'optimization', limit: 20 }), []);
  return (
    <div className="stack">
      <h1>Optimizations</h1>
      <Card title="Recent optimizations">
        <ErrorBox error={jobs.error} />
        <table className="table">
          <thead>
            <tr>
              <th>Job</th>
              <th>Name</th>
              <th>Status</th>
              <th>Progress</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {jobs.data?.items?.map((j) => (
              <tr key={j.id}>
                <td>
                  <JobLink job={j} />
                </td>
                <td>{j.name || '—'}</td>
                <td>
                  <StatusBadge status={j.status} />
                </td>
                <td>
                  <Progress value={j.status === 'completed' ? 1 : j.progress} />
                </td>
                <td>{fmtTime(j.createdAt)}</td>
              </tr>
            ))}
            {jobs.data && !jobs.data.items?.length && (
              <tr>
                <td colSpan={5} className="muted">
                  No optimizations yet.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </Card>
      <h2>New optimization</h2>
      <OptimizationForm />
    </div>
  );
}
