import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, FleetEvent } from '../api.service';

const SHOW_LIMIT = 500;

interface Row {
  e: FleetEvent;
  label: string;
}

/** "[2,"uid","Heartbeat",{}]" -> "Heartbeat"; results and errors are labelled by frame type. */
function labelFor(e: FleetEvent): string {
  if (e.kind === 'log') return 'log';
  try {
    const frame = JSON.parse(e.text) as unknown[];
    if (frame[0] === 2) return String(frame[2]);
    if (frame[0] === 3) return 'CallResult';
    if (frame[0] === 4) return `CallError ${String(frame[2] ?? '')}`.trim();
  } catch { /* not JSON */ }
  return 'frame';
}

@Component({
  selector: 'app-log',
  imports: [FormsModule, DatePipe],
  template: `
    <div class="page">
      <div class="row" style="margin-bottom: 12px">
        <h1 style="margin: 0">Message log</h1>
        <span class="muted small">newest first &middot; showing {{ rows().length }} of {{ matching() }}</span>
        <span class="spacer"></span>
        <button class="danger" (click)="api.clearEvents()">Clear</button>
      </div>

      <div class="card row">
        <label class="field">Charge point
          <select [ngModel]="node()" (ngModelChange)="node.set($event)">
            <option value="">All</option>
            @for (n of nodeIds(); track n) { <option [value]="n">{{ n }}</option> }
          </select>
        </label>
        <label class="check"><input type="checkbox" [ngModel]="showOut()" (ngModelChange)="showOut.set($event)" /> sent &rarr;</label>
        <label class="check"><input type="checkbox" [ngModel]="showIn()" (ngModelChange)="showIn.set($event)" /> received &larr;</label>
        <label class="check"><input type="checkbox" [ngModel]="showLog()" (ngModelChange)="showLog.set($event)" /> info</label>
        <input [ngModel]="search()" (ngModelChange)="search.set($event)" placeholder="search text or action" style="flex: 1; min-width: 180px" />
      </div>

      <div class="card" style="padding: 0">
        @if (!rows().length) { <div class="empty">Nothing to show yet.</div> }
        @for (r of rows(); track r.e.seq) {
          <div class="line" [class.open]="open() === r.e.seq" (click)="toggle(r.e.seq)" (keydown.enter)="toggle(r.e.seq)" tabindex="0">
            <span class="mono muted">{{ r.e.time | date: 'HH:mm:ss.SSS' }}</span>
            <span class="mono node">{{ r.e.node || '-' }}</span>
            <span class="dir" [class.out]="r.e.kind === 'wire-out'" [class.in]="r.e.kind === 'wire-in'">
              {{ r.e.kind === 'wire-out' ? '→' : r.e.kind === 'wire-in' ? '←' : 'i' }}
            </span>
            <span class="label">{{ r.label }}</span>
            <span class="mono text">{{ r.e.text }}</span>
            @if (open() === r.e.seq) {
              <pre class="full">{{ pretty(r.e.text) }}</pre>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .line {
      display: grid;
      grid-template-columns: 96px 78px 18px 150px 1fr;
      gap: 8px;
      padding: 4px 12px;
      border-bottom: 1px solid var(--border);
      cursor: pointer;
      align-items: baseline;
    }
    .line:hover, .line.open { background: var(--bg); }
    .text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .line.open .text { white-space: normal; display: none; }
    .full { grid-column: 1 / -1; margin: 4px 0 8px; }
    .dir { font-weight: 700; text-align: center; }
    .dir.out { color: var(--accent); }
    .dir.in { color: var(--ok); }
    .label { font-weight: 600; }
  `,
})
export class LogPage {
  protected readonly api = inject(ApiService);

  protected readonly node = signal('');
  protected readonly search = signal('');
  protected readonly showOut = signal(true);
  protected readonly showIn = signal(true);
  protected readonly showLog = signal(true);
  protected readonly open = signal<number | null>(null);

  protected readonly nodeIds = computed(() =>
    [...new Set(this.api.events().map((e) => e.node).filter(Boolean))].sort());

  private readonly filtered = computed(() => {
    const node = this.node();
    const q = this.search().trim().toLowerCase();
    return this.api.events().filter((e) => {
      if (node && e.node !== node) return false;
      if (e.kind === 'wire-out' && !this.showOut()) return false;
      if (e.kind === 'wire-in' && !this.showIn()) return false;
      if (e.kind === 'log' && !this.showLog()) return false;
      return !q || e.text.toLowerCase().includes(q) || labelFor(e).toLowerCase().includes(q);
    });
  });

  protected readonly matching = computed(() => this.filtered().length);
  protected readonly rows = computed<Row[]>(() =>
    this.filtered().slice(-SHOW_LIMIT).reverse().map((e) => ({ e, label: labelFor(e) })));

  protected toggle(seq: number): void { this.open.update((cur) => (cur === seq ? null : seq)); }

  protected pretty(text: string): string {
    try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
  }
}
