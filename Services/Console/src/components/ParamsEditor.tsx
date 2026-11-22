export type ParamRow = { key: string; value: string };

export const toParameters = (rows: ParamRow[]) =>
  Object.fromEntries(rows.filter((r) => r.key.trim()).map((r) => [r.key.trim(), r.value]));

export function ParamsEditor({ rows, onChange }: { rows: ParamRow[]; onChange: (rows: ParamRow[]) => void }) {
  const set = (i: number, patch: Partial<ParamRow>) => onChange(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  return (
    <div className="params">
      {rows.map((r, i) => (
        <div className="row" key={i}>
          <input placeholder="name" value={r.key} onChange={(e) => set(i, { key: e.target.value })} aria-label="Parameter name" />
          <input placeholder="value" value={r.value} onChange={(e) => set(i, { value: e.target.value })} aria-label="Parameter value" />
          <button type="button" className="ghost" onClick={() => onChange(rows.filter((_, j) => j !== i))} aria-label="Remove parameter">
            ×
          </button>
        </div>
      ))}
      <button type="button" className="ghost" onClick={() => onChange([...rows, { key: '', value: '' }])}>
        + parameter
      </button>
    </div>
  );
}
