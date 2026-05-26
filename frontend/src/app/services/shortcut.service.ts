import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface Shortcut {
  id: number;
  url: string;
  name: string;
  order: number;
}

@Injectable({ providedIn: 'root' })
export class ShortcutService {
  private http = inject(HttpClient);

  private _shortcuts = signal<Shortcut[]>([]);
  readonly shortcuts = this._shortcuts.asReadonly();

  constructor() {
    this.load();
  }

  load(): void {
    this.http.get<Shortcut[]>('/api/shortcuts').subscribe({
      next: (shortcuts) => this._shortcuts.set(shortcuts),
      error: (e) => console.error('Failed to load shortcuts', e),
    });
  }

  add(url: string): Promise<void> {
    return new Promise((resolve, reject) => {
      this.http.post<Shortcut>('/api/shortcuts', { url }).subscribe({
        next: (created) => {
          this._shortcuts.update((list) => [...list, created]);
          resolve();
        },
        error: (e) => {
          console.error('Failed to add shortcut', e);
          reject(e);
        },
      });
    });
  }

  rename(id: number, name: string): void {
    this.http.patch<Shortcut>(`/api/shortcuts/${id}/name`, { name }).subscribe({
      next: (updated) => {
        this._shortcuts.update((list) =>
          list.map((s) => (s.id === id ? { ...s, name: updated.name } : s))
        );
      },
      error: (e) => console.error('Failed to rename shortcut', e),
    });
  }

  reorder(orderedIds: number[]): void {
    this.http.patch<Shortcut[]>('/api/shortcuts/reorder', orderedIds).subscribe({
      next: (updated) => this._shortcuts.set(updated),
      error: (e) => console.error('Failed to reorder shortcuts', e),
    });
  }

  delete(id: number): void {
    this._shortcuts.update((list) => list.filter((s) => s.id !== id));
    this.http.delete(`/api/shortcuts/${id}`).subscribe({
      error: (e) => {
        console.error('Failed to delete shortcut', e);
        // Reload to restore state on failure
        this.load();
      },
    });
  }
}
