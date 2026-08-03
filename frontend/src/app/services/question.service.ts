import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface QuestionOption {
  key: string;
  label: string;
  description?: string;
}

export type QuestionState = 'open' | 'answered' | 'applied' | 'dismissed' | 'failed';

export interface InboxQuestion {
  id: number;
  fingerprint: string;
  source: string;
  subjectId: string | null;
  detectionType: string;
  title: string;
  body: string;
  options: QuestionOption[] | null;
  pinnedDelete: { heading?: string; contentHash?: string } | null;
  subjectHash: string | null;
  state: QuestionState;
  answerKey: string | null;
  answerText: string | null;
  answeredAt: string | null;
  appliedAt: string | null;
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

/** The job's cadence is hourly; well past that means something broke. */
const STALE_AFTER_MS = 3 * 60 * 60 * 1000;
const JOB_NAME = 'decision-inbox';

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
  }

  answer(id: number, key: string, text?: string): void {
    // Optimistic — the card should stop being actionable the instant you click.
    this.patchLocal(id, { state: 'answered', answerKey: key, answerText: text ?? null });
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
