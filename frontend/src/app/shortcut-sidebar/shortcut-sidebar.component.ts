import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ShortcutService, Shortcut } from '../services/shortcut.service';

@Component({
  selector: 'app-shortcut-sidebar',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './shortcut-sidebar.component.html',
  styleUrl: './shortcut-sidebar.component.scss',
})
export class ShortcutSidebarComponent {
  readonly service = inject(ShortcutService);

  // Drag state
  draggingIndex = signal<number | null>(null);
  dragOverIndex = signal<number | null>(null);

  // Inline edit state
  editingId = signal<number | null>(null);
  editingName = signal('');

  // Add input state
  addUrl = signal('');
  isAdding = signal(false);

  faviconUrl(url: string): string {
    try {
      const hostname = new URL(url.startsWith('http') ? url : `https://${url}`).hostname;
      return `https://www.google.com/s2/favicons?domain=${hostname}&sz=32`;
    } catch {
      return '';
    }
  }

  openUrl(shortcut: Shortcut): void {
    window.location.href = shortcut.url.startsWith('http') ? shortcut.url : `https://${shortcut.url}`;
  }

  // ── Drag & Drop ───────────────────────────────────────────────────────────

  onDragStart(event: DragEvent, index: number): void {
    this.draggingIndex.set(index);
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
    }
  }

  onDragOver(event: DragEvent, index: number): void {
    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'move';
    }
    this.dragOverIndex.set(index);
  }

  onDragLeave(): void {
    // Only clear dragOver when leaving the list area (handled by dragend)
  }

  onDrop(event: DragEvent, dropIndex: number): void {
    event.preventDefault();
    const fromIndex = this.draggingIndex();
    if (fromIndex === null || fromIndex === dropIndex) {
      this.clearDragState();
      return;
    }

    const list = [...this.service.shortcuts()];
    const [moved] = list.splice(fromIndex, 1);
    list.splice(dropIndex, 0, moved);

    // Update order values and reorder via service (optimistic update handled inside)
    const reordered = list.map((s, i) => ({ ...s, order: i }));
    this.service.reorder(reordered.map((s) => s.id));

    this.clearDragState();
  }

  onDragEnd(): void {
    this.clearDragState();
  }

  private clearDragState(): void {
    this.draggingIndex.set(null);
    this.dragOverIndex.set(null);
  }

  // ── Inline rename ─────────────────────────────────────────────────────────

  startEdit(shortcut: Shortcut): void {
    this.editingId.set(shortcut.id);
    this.editingName.set(shortcut.name);
  }

  commitEdit(id: number): void {
    if (this.editingId() !== id) return; // guard double-fire
    const name = this.editingName().trim();
    if (name) {
      this.service.rename(id, name);
    }
    this.editingId.set(null);
  }

  cancelEdit(): void {
    this.editingId.set(null);
  }

  // ── Add shortcut ──────────────────────────────────────────────────────────

  async onAddKeydown(event: KeyboardEvent): Promise<void> {
    if (event.key !== 'Enter') return;
    const url = this.addUrl().trim();
    if (!url || this.isAdding()) return;

    this.isAdding.set(true);
    try {
      await this.service.add(url);
      this.addUrl.set('');
    } finally {
      this.isAdding.set(false);
    }
  }

  // ── Delete ────────────────────────────────────────────────────────────────

  deleteShortcut(event: MouseEvent, id: number): void {
    event.stopPropagation();
    this.service.delete(id);
  }
}
