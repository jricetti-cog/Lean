import createClient from 'openapi-fetch';
import type { components, paths } from './schema';

export type Schemas = components['schemas'];
export type Job = Schemas['Job'];
export type JobStatus = Schemas['JobStatus'];
export type JobKind = Schemas['JobKind'];
export type ResultSummary = Schemas['ResultSummary'];
export type OptimizationState = Schemas['OptimizationState'];
export type ChildBacktest = Schemas['ChildBacktest'];
export type BacktestSpec = Schemas['BacktestSpec'];
export type OptimizationSpec = Schemas['OptimizationSpec'];
export type LiveSpec = Schemas['LiveSpec'];
export type AlgorithmRef = Schemas['AlgorithmRef'];
export type LogLine = Schemas['LogLine'];
export type Problem = Schemas['Problem'];
export type LeanObject = Record<string, unknown>;
export type JobListQuery = NonNullable<paths['/api/v1/jobs']['get']['parameters']['query']>;

/** `GET /api/v1/jobs` with `items` narrowed to `Job` (`JobPage` is `Page & {items: Job[]}`, which TS widens to unknown). */
export async function listJobs(query: JobListQuery): Promise<{ items: Job[]; next?: string | null }> {
  const page = ok(await api.GET('/api/v1/jobs', { params: { query } }));
  return { items: (page.items ?? []) as Job[], next: page.next };
}

export type ChildQuery = NonNullable<paths['/api/v1/optimizations/{id}/children']['get']['parameters']['query']>;

/** `GET /api/v1/optimizations/{id}/children` (optimizer via the gateway) with `items` narrowed to `ChildBacktest`. */
export async function listOptimizationChildren(id: string, query: ChildQuery = {}): Promise<{ items: ChildBacktest[]; next?: string | null }> {
  const page = ok(await api.GET('/api/v1/optimizations/{id}/children', { params: { path: { id }, query } }));
  return { items: (page.items ?? []) as ChildBacktest[], next: page.next };
}

/**
 * Typed client generated (openapi-typescript) from the gateway's public OpenAPI document. Requests are same-origin
 * `/api/v1/...`; the console's nginx (or the Vite dev proxy) forwards them to api-gateway and injects the API key
 * from its environment, so the key never ships in the bundle.
 */
export const api = createClient<paths>({ baseUrl: '' });

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly problem?: Problem) {
    super(problem?.detail ?? problem?.title ?? `HTTP ${status}`);
  }
}

/** Unwraps an openapi-fetch result, throwing the RFC 9457 problem on failure. */
export function ok<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || !result.response.ok) {
    throw new ApiError(result.response.status, result.error as Problem | undefined);
  }
  return result.data as T;
}

/** Uploads an algorithm source (`POST /api/v1/algorithms`, multipart) and returns its `AlgorithmRef`. */
export async function uploadAlgorithm(language: 'CSharp' | 'Python', source: string, fileName?: string): Promise<AlgorithmRef> {
  const name = fileName ?? (language === 'Python' ? 'main.py' : 'Main.cs');
  return ok(
    await api.POST('/api/v1/algorithms', {
      body: { file: name, language } as never,
      bodySerializer: () => {
        const form = new FormData();
        form.append('file', new Blob([source], { type: 'text/plain' }), name);
        form.append('language', language);
        return form;
      },
    }),
  ) as AlgorithmRef;
}

/** Follows every `next` cursor of a paged collection (bounded). */
export async function allPages<T>(fetchPage: (cursor?: string) => Promise<{ items?: T[]; next?: string | null }>, max = 20): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | undefined;
  for (let i = 0; i < max; i++) {
    const page = await fetchPage(cursor);
    items.push(...(page.items ?? []));
    if (!page.next) break;
    cursor = page.next;
  }
  return items;
}

export const TERMINAL: ReadonlySet<JobStatus> = new Set(['completed', 'failed', 'cancelled', 'stale'] as JobStatus[]);

/** Public download URL (gateway 302s to a presigned MinIO URL). */
export const resultDownloadUrl = (id: string) => `/api/v1/jobs/${id}/result/download`;
export const reportDownloadUrl = (id: string, format: 'html' | 'pdf') => `/api/v1/reports/${id}/${format}`;
