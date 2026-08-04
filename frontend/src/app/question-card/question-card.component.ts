import { Component, computed, inject, input, signal } from '@angular/core';
import { InboxQuestion, QuestionService } from '../services/question.service';

@Component({
  selector: 'app-question-card',
  standalone: true,
  templateUrl: './question-card.component.html',
  styleUrl: './question-card.component.scss',
})
export class QuestionCardComponent {
  readonly question = input.required<InboxQuestion>();

  private questionService = inject(QuestionService);

  readonly freeText = signal('');
  readonly expanded = signal<string | null>(null);

  readonly options = computed(() => this.question().options ?? []);

  readonly recommendation = computed(() => this.question().recommendation ?? null);

  /**
   * What to call the recommended option. Falls back to the raw key when the
   * producer named something that isn't on the card — a mismatch costs the
   * marker, not the rationale, which is the part worth reading anyway.
   */
  readonly recommendedLabel = computed(() => {
    const key = this.recommendation()?.key;
    if (!key) return null;
    return this.options().find((o) => o.key === key)?.label ?? key;
  });

  readonly subjectUrl = computed(() => {
    const id = this.question().subjectId;
    return id && /^RET-\d+$/.test(id)
      ? `https://linear.app/retrospectivefun/issue/${id}`
      : null;
  });

  /**
   * Rendered as a hint on the card because the delete is the one irreversible-
   * looking operation — worth seeing before you pick, not after.
   */
  readonly deleteTarget = computed(() => this.question().pinnedDelete?.heading ?? null);

  isRecommended(key: string): boolean {
    return this.recommendation()?.key === key;
  }

  toggleDescription(key: string): void {
    this.expanded.update((current) => (current === key ? null : key));
  }

  choose(key: string): void {
    const text = this.freeText().trim();
    this.questionService.answer(this.question().id, key, text || undefined);
  }

  dismiss(): void {
    this.questionService.dismiss(this.question().id);
  }
}
