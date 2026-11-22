import { useEffect, useMemo, useState } from 'react';
import { uploadAlgorithm, type AlgorithmRef } from '../api/client';
import { CodeEditor } from './CodeEditor';

export interface Sample {
  language: 'CSharp' | 'Python';
  name: string;
  typeName: string;
  file: string;
  description: string;
  regression: boolean;
}

export interface AlgorithmChoice {
  language: 'CSharp' | 'Python';
  sample?: Sample;
  source: string;
  edited: boolean;
}

let samplesCache: Promise<Sample[]> | undefined;
export const loadSamples = () => (samplesCache ??= fetch('/samples/index.json').then((r) => (r.ok ? r.json() : [])));

const DEFAULT_SAMPLE = 'BasicTemplateAlgorithm';

/**
 * Turns the editor state into an `AlgorithmRef`: an unedited C# sample runs as a `builtin` class compiled into the
 * worker image; anything else (Python, edited C#) is uploaded through `POST /api/v1/algorithms` first.
 */
export async function resolveAlgorithm(choice: AlgorithmChoice): Promise<AlgorithmRef> {
  if (choice.language === 'CSharp' && choice.sample && !choice.edited) {
    return { source: 'builtin', language: 'CSharp', typeName: choice.sample.typeName } as AlgorithmRef;
  }
  return uploadAlgorithm(choice.language, choice.source, choice.language === 'Python' ? 'main.py' : `${choice.sample?.name ?? 'Main'}.cs`);
}

export function AlgorithmPicker({ value, onChange, height = 420 }: { value?: AlgorithmChoice; onChange: (c: AlgorithmChoice) => void; height?: number }) {
  const [samples, setSamples] = useState<Sample[]>([]);
  const [filter, setFilter] = useState('');
  const language = value?.language ?? 'Python';

  useEffect(() => {
    loadSamples().then(setSamples);
  }, []);

  const pick = async (sample: Sample | undefined, lang = language) => {
    if (!sample) return onChange({ language: lang, source: '', edited: true });
    const source = await fetch(`/samples/${sample.file}`).then((r) => r.text());
    onChange({ language: sample.language, sample, source, edited: false });
  };

  useEffect(() => {
    if (!value && samples.length) pick(samples.find((s) => s.language === language && s.name === DEFAULT_SAMPLE));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [samples]);

  const options = useMemo(
    () =>
      samples.filter(
        (s) => s.language === language && (s.file === value?.sample?.file || s.name.toLowerCase().includes(filter.toLowerCase())),
      ),
    [samples, language, filter, value?.sample?.file],
  );

  return (
    <div className="picker">
      <div className="row">
        <div className="segmented" role="tablist">
          {(['Python', 'CSharp'] as const).map((l) => (
            <button
              key={l}
              type="button"
              role="tab"
              aria-selected={language === l}
              className={language === l ? 'on' : ''}
              onClick={() => pick(samples.find((s) => s.language === l && s.name === DEFAULT_SAMPLE), l)}
            >
              {l === 'CSharp' ? 'C#' : 'Python'}
            </button>
          ))}
        </div>
        <input placeholder="Filter samples…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter samples" />
        <select
          aria-label="Sample algorithm"
          value={value?.sample?.file ?? ''}
          onChange={(e) => pick(samples.find((s) => s.file === e.target.value))}
        >
          <option value="">— blank —</option>
          {options.map((s) => (
            <option key={s.file} value={s.file}>
              {s.name}
            </option>
          ))}
        </select>
        <span className="muted small">{options.length} samples from Algorithm.{language === 'CSharp' ? 'CSharp' : 'Python'}</span>
      </div>
      {value?.sample?.description && <p className="muted small">{value.sample.description}</p>}
      <div style={{ height }}>
        <CodeEditor value={value?.source ?? ''} language={language} onChange={(source) => value && onChange({ ...value, source, edited: true })} />
      </div>
      <p className="muted small">
        {language === 'CSharp'
          ? value?.edited
            ? 'Edited C# is uploaded as a .cs file; the stock worker image runs C# samples as built-in classes.'
            : `Runs built-in ${value?.sample?.typeName ?? ''} from the worker image.`
          : 'Uploaded as main.py through POST /api/v1/algorithms.'}
      </p>
    </div>
  );
}
