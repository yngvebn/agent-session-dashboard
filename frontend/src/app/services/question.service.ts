import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface QuestionOption {
  key: string;
  label: string;
  description?: string;
}

/**
 * The producer's own pick, shown so you're calibrating a proposal rather than
 * starting cold. Advisory: the applier acts on the answer, never on this.
 * `key` may name an option that is no longer on the card — the rationale still
 * reads, the marker just doesn't appear.
 */
export interface QuestionRecommendation {
  key: string;
  rationale?: string;
  confidence?: 'low' | 'medium' | 'high';
}

export type QuestionState = 'open' | 'answered' | 'applied' | 'dismissed' | 'failed';

export interface InboxQuestion {
  id: number;
  fingerprint: string;
  source: string;
  subjectId: string | null;
  detectionType: string;
  /** Identifies the specific finding — two questions can share subject and type. */
  detectionKey: string | null;
  title: string;
  body: string;
  options: QuestionOption[] | null;
  recommendation: QuestionRecommendation | null;
  pinnedDelete: { heading?: string; contentHash?: string } | null;
  subjectHash: string | null;
  state: QuestionState;
  answerKey: string | null;
  answerText: string | null;
  answeredAt: string | null;
  appliedAt: string | null;
  dismissedAt: string | null;
  note: string | null;
  createdAt: string;
}

export interface JobRun {
  id: number;
  jobName: string;
  startedAt: string;
  finishedAt: string | null;
  outcome: 'ok' | 'skipped' | 'failed' | null;
  detail: string | null;
  created: number;
  applied: number;
}

/** The job's cadence is 30 minutes; well past that means something broke. */
const STALE_AFTER_MS = 3 * 60 * 60 * 1000;
const JOB_NAME = 'decision-inbox';

/**
 * Mirrors `JobRunService.RunLease`. An unfinished run older than this was killed
 * rather than still working, so the button must not stay disabled on it forever.
 */
const RUN_LEASE_MS = 25 * 60 * 1000;

@Injectable({ providedIn: 'root' })
export class QuestionService {
  private http = inject(HttpClient);

  private _questions = signal<InboxQuestion[]>([]);
  private _lastRun = signal<JobRun | null>(null);
  /** Ticks so "last checked Nh ago" ages without needing a new event. */
  private now = signal(Date.now());

  readonly questions = this._questions.asReadonly();
  readonly lastRun = this._lastRun.asReadonly();

  /** Only open questions are actionable; answered ones await the next run. */
  readonly openQuestions = computed(() =>
    this._questions().filter((q) => q.state === 'open' || q.state === 'failed')
  );

  readonly pendingQuestions = computed(() =>
    this._questions().filter((q) => q.state === 'answered')
  );

  private _runRequested = signal(false);
  /** A press the runner has not picked up yet. */
  readonly runRequested = this._runRequested.asReadonly();

  /**
   * A run is in flight when the newest row has no `finishedAt` — and is still
   * within its lease. Past the lease it was killed, and continuing to report it as
   * running would leave the button dead until someone noticed.
   */
  readonly runInFlight = computed(() => {
    const run = this._lastRun();
    if (!run || run.finishedAt) return false;
    return this.now() - new Date(run.startedAt).getTime() < RUN_LEASE_MS;
  });

  readonly heartbeatAge = computed(() => {
    const run = this._lastRun();
    if (!run) return null;
    return this.now() - new Date(run.finishedAt ?? run.startedAt).getTime();
  });

  /**
   * The point of the heartbeat: without it an empty inbox reads as "nothing to
   * decide" when it might mean the job died three weeks ago.
   */
  readonly isStale = computed(() => {
    const age = this.heartbeatAge();
    return age === null || age > STALE_AFTER_MS;
  });

  readonly heartbeatLabel = computed(() => {
    const run = this._lastRun();
    if (!run) return 'never checked';
    if (this.runInFlight()) return 'triage running…';

    const age = this.heartbeatAge() ?? 0;
    const mins = Math.floor(age / 60_000);
    const when =
      mins < 1 ? 'just now'
      : mins < 60 ? `${mins}m ago`
      : mins < 1440 ? `${Math.floor(mins / 60)}h ago`
      : `${Math.floor(mins / 1440)}d ago`;

    return run.outcome === 'failed' ? `last run failed ${when}` : `last checked ${when}`;
  });

  constructor() {
    this.load();
    setInterval(() => this.now.set(Date.now()), 30_000);
  }

  load(): void {
    this.http.get<InboxQuestion[]>('/api/questions').subscribe({
      next: (questions) => this._questions.set(questions),
      error: (e) => console.error('Failed to load questions', e),
    });
    this.loadHeartbeat();
  }

  loadHeartbeat(): void {
    this.http
      .get<JobRun[]>(`/api/jobs/runs?name=${JOB_NAME}&limit=1`)
      .subscribe({
        next: (runs) => this._lastRun.set(runs[0] ?? null),
        error: () => {},
      });
    // Server-side, so a reloaded tab still shows a press that hasn't been picked up.
    this.http
      .get<unknown[]>(`/api/jobs/requests?name=${JOB_NAME}`)
      .subscribe({
        next: (pending) => this._runRequested.set(pending.length > 0),
        error: () => {},
      });
  }

  /**
   * Asks for a run. The dashboard is containerised and cannot start a host process,
   * so this only records the request — the runner ticks every minute and claims it.
   */
  requestRun(): void {
    this._runRequested.set(true);
    this.http.post(`/api/jobs/requests`, { jobName: JOB_NAME }).subscribe({
      error: (e) => {
        console.error('Failed to request a run', e);
        this._runRequested.set(false);
      },
    });
  }

  /** Called by the SSE service so live updates and REST share one store. */
  upsert(question: InboxQuestion): void {
    this._questions.update((list) => {
      const idx = list.findIndex((q) => q.id === question.id);
      if (idx < 0) return [...list, question];
      const next = [...list];
      next[idx] = question;
      return next;
    });
  }

  setLastRun(run: JobRun): void {
    if (run.jobName !== JOB_NAME) return;
    this._lastRun.set(run);
    // A run starting is the runner having claimed the request, so the press is served.
    if (!run.finishedAt) this._runRequested.set(false);
  }

  /**
   * Answers a question. Pass `key` to choose an option, or omit it and pass `text` to
   * write your own — the server stores that under the reserved `custom` key.
   */
  answer(id: number, key: string | null, text?: string): void {
    // Optimistic — the card should stop being actionable the instant you click.
    this.patchLocal(id, { state: 'answered', answerKey: key ?? 'custom', answerText: text ?? null });
    this.http.post<InboxQuestion>(`/api/questions/${id}/answer`, { key, text }).subscribe({
      next: (updated) => this.upsert(updated),
      error: (e) => {
        console.error('Failed to answer question', e);
        this.load();
      },
    });
  }

  dismiss(id: number): void {
    this.patchLocal(id, { state: 'dismissed' });
    this.http.post<InboxQuestion>(`/api/questions/${id}/dismiss`, {}).subscribe({
      next: (updated) => this.upsert(updated),
      error: (e) => {
        console.error('Failed to dismiss question', e);
        this.load();
      },
    });
  }

  private patchLocal(id: number, patch: Partial<InboxQuestion>): void {
    this._questions.update((list) =>
      list.map((q) => (q.id === id ? { ...q, ...patch } : q))
    );
  }
}
