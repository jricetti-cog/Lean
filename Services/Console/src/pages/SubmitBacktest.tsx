import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { api, ok, type BacktestSpec } from '../api/client';
import { AlgorithmPicker, resolveAlgorithm, type AlgorithmChoice } from '../components/AlgorithmPicker';
import { ParamsEditor, toParameters, type ParamRow } from '../components/ParamsEditor';
import { Card, ErrorBox } from '../components/ui';

export interface BacktestFields {
  name: string;
  startDate: string;
  endDate: string;
  cash: string;
  maxRuntimeSeconds: string;
  fetchMissingData: boolean;
}

export const emptyFields: BacktestFields = { name: '', startDate: '', endDate: '', cash: '', maxRuntimeSeconds: '3600', fetchMissingData: false };

export function backtestSpec(fields: BacktestFields, algorithm: BacktestSpec['algorithm'], params: ParamRow[]): BacktestSpec {
  return {
    name: fields.name || undefined,
    algorithm,
    parameters: toParameters(params),
    startDate: fields.startDate || undefined,
    endDate: fields.endDate || undefined,
    cash: fields.cash ? Number(fields.cash) : undefined,
    fetchMissingData: fields.fetchMissingData,
    maxRuntimeSeconds: Number(fields.maxRuntimeSeconds || 3600),
  };
}

export function BacktestFieldsForm({ fields, onChange }: { fields: BacktestFields; onChange: (f: BacktestFields) => void }) {
  const set = (patch: Partial<BacktestFields>) => onChange({ ...fields, ...patch });
  return (
    <div className="grid-form">
      <label>
        Name <input value={fields.name} onChange={(e) => set({ name: e.target.value })} placeholder="optional" />
      </label>
      <label>
        Start date <input type="date" value={fields.startDate} onChange={(e) => set({ startDate: e.target.value })} />
      </label>
      <label>
        End date <input type="date" value={fields.endDate} onChange={(e) => set({ endDate: e.target.value })} />
      </label>
      <label>
        Cash <input type="number" min="0" value={fields.cash} onChange={(e) => set({ cash: e.target.value })} placeholder="algorithm default" />
      </label>
      <label>
        Max runtime (s) <input type="number" min="1" value={fields.maxRuntimeSeconds} onChange={(e) => set({ maxRuntimeSeconds: e.target.value })} />
      </label>
      <label className="check">
        <input type="checkbox" checked={fields.fetchMissingData} onChange={(e) => set({ fetchMissingData: e.target.checked })} /> Fetch missing data
      </label>
    </div>
  );
}

export default function SubmitBacktest() {
  const navigate = useNavigate();
  const [algorithm, setAlgorithm] = useState<AlgorithmChoice>();
  const [fields, setFields] = useState(emptyFields);
  const [params, setParams] = useState<ParamRow[]>([]);
  const [error, setError] = useState<Error>();
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!algorithm) return;
    setBusy(true);
    setError(undefined);
    try {
      const ref = await resolveAlgorithm(algorithm);
      const job = ok(await api.POST('/api/v1/backtests', { body: backtestSpec({ ...fields, name: fields.name || algorithm.sample?.name || '' }, ref, params) }));
      navigate(`/jobs/${job.id}`);
    } catch (err) {
      setError(err as Error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} className="stack">
      <h1>New backtest</h1>
      <Card title="Algorithm">
        <AlgorithmPicker value={algorithm} onChange={setAlgorithm} />
      </Card>
      <Card title="Settings">
        <BacktestFieldsForm fields={fields} onChange={setFields} />
        <h3>Parameters</h3>
        <ParamsEditor rows={params} onChange={setParams} />
      </Card>
      <ErrorBox error={error} />
      <div className="row end">
        <button type="submit" className="primary" disabled={busy || !algorithm}>
          {busy ? 'Submitting…' : 'Run backtest'}
        </button>
      </div>
    </form>
  );
}
