import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ConnectorStatus =
  | 'Available' | 'Preparing' | 'Charging' | 'SuspendedEVSE' | 'SuspendedEV'
  | 'Finishing' | 'Reserved' | 'Unavailable' | 'Faulted';

export interface TransactionState {
  id: number | null;
  idTag: string;
  active: boolean;
  startedAt: string;
  energyWh: number;
}

export interface ConnectorState {
  id: number;
  status: ConnectorStatus;
  errorCode: string;
  errorInfo: string;
  operative: boolean;
  plugged: boolean;
  reservationId: number | null;
  reservedForTag: string | null;
  reservationExpiry: string | null;
  meter: { energyWh: number; powerOfferedW: number; activePowerW: number; soc: number; timeScale: number };
  transaction: TransactionState | null;
}

export interface NodeState {
  id: string;
  connected: boolean;
  booted: boolean;
  clockSynced: boolean;
  clock: string | null;
  connectors: ConnectorState[];
}

export interface FleetEvent {
  seq: number;
  time: string;
  node: string;
  kind: 'log' | 'wire-in' | 'wire-out';
  text: string;
}

export interface ConnectRequest {
  baseUrl: string;
  count: number;
  idPrefix?: string;
  first?: number;
  id?: string;
  connectors?: number;
  powerW?: number;
  vendor?: string;
  model?: string;
  firmware?: string;
  heartbeatInterval?: number;
  meterInterval?: number;
  speed?: number;
  user?: string;
  password?: string;
  tag?: string;
  auto?: boolean;
  suspendedEv?: boolean;
  passive?: boolean;
  replace?: boolean;
}

export interface ConfigKey { name: string; readOnly: boolean; mutability: string; value: string }
export interface AuthEntry { idTag: string; info: string }
export interface ProfileRow {
  connectorId: number; id: number; stackLevel: number; purpose: string; kind: string;
  transactionId: number | null; raw: string;
}

export const FAULT_CODES = [
  'ground', 'overcurrent', 'overvoltage', 'undervoltage', 'temperature', 'lock',
  'comms', 'reader', 'meter', 'switch', 'internal', 'other',
] as const;

const MAX_EVENTS = 2000;

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  readonly nodes = signal<NodeState[]>([]);
  readonly events = signal<FleetEvent[]>([]);
  /** True while the live stream to the backend is open. */
  readonly live = signal(false);
  /** Last command error / info, shown as a toast. */
  readonly notice = signal<{ kind: 'error' | 'ok'; text: string } | null>(null);

  private source?: EventSource;
  private noticeTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    this.openStream();
  }

  private openStream(): void {
    const es = new EventSource('/api/events');
    this.source = es;
    es.onopen = () => {
      this.live.set(true);
      void this.loadBacklog();
    };
    es.onerror = () => this.live.set(false);   // EventSource reconnects by itself
    es.addEventListener('state', (e) => {
      this.nodes.set((JSON.parse((e as MessageEvent).data) as { nodes: NodeState[] }).nodes);
    });
    es.addEventListener('log', (e) => this.addEvents([JSON.parse((e as MessageEvent).data) as FleetEvent]));
  }

  private async loadBacklog(): Promise<void> {
    try {
      this.addEvents(await firstValueFrom(this.http.get<FleetEvent[]>('/api/log?limit=500')));
    } catch { /* stream will fill it in */ }
  }

  private addEvents(incoming: FleetEvent[]): void {
    this.events.update((cur) => {
      const last = cur.length ? cur[cur.length - 1].seq : 0;
      const fresh = incoming.filter((e) => e.seq > last);
      if (!fresh.length) return cur;
      const merged = cur.concat(fresh);
      return merged.length > MAX_EVENTS ? merged.slice(merged.length - MAX_EVENTS) : merged;
    });
  }

  clearEvents(): void {
    this.events.set([]);
    this.http.delete('/api/log').subscribe();
  }

  // ---- commands ------------------------------------------------------------

  /** POST a command; shows the backend's error message as a notice. Returns undefined on failure. */
  async command<T = unknown>(path: string, body?: unknown, okText?: string): Promise<T | undefined> {
    try {
      const res = await firstValueFrom(this.http.post<T>('/api' + path, body ?? {}));
      // Command endpoints answer with the node's fresh state - apply it right away.
      const maybeNode = res as unknown as NodeState | null;
      if (maybeNode && typeof maybeNode === 'object' && 'connectors' in maybeNode) {
        this.nodes.update((ns) => ns.map((n) => (n.id === maybeNode.id ? maybeNode : n)));
      }
      if (okText) this.toast('ok', okText);
      return res;
    } catch (err) {
      this.toast('error', this.describe(err));
      return undefined;
    }
  }

  connect(req: ConnectRequest) {
    return this.command<{ added: string[] }>('/fleet/connect', req);
  }

  disconnect(ids?: string[]) {
    return this.command<{ removed: number }>('/fleet/disconnect', { ids });
  }

  async get<T>(path: string): Promise<T | undefined> {
    try {
      return await firstValueFrom(this.http.get<T>('/api' + path));
    } catch (err) {
      this.toast('error', this.describe(err));
      return undefined;
    }
  }

  async setConfig(id: string, key: string, value: string): Promise<string | undefined> {
    try {
      const r = await firstValueFrom(
        this.http.put<{ result: string }>(`/api/cp/${encodeURIComponent(id)}/config/${encodeURIComponent(key)}`, { value }));
      return r.result;
    } catch (err) {
      this.toast('error', this.describe(err));
      return undefined;
    }
  }

  toast(kind: 'error' | 'ok', text: string): void {
    clearTimeout(this.noticeTimer);
    this.notice.set({ kind, text });
    this.noticeTimer = setTimeout(() => this.notice.set(null), kind === 'error' ? 6000 : 2500);
  }

  private describe(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      const body = err.error as { error?: string } | null;
      if (body?.error) return body.error;
      if (err.status === 0) return 'Cannot reach the backend. Is it running?';
      return `${err.status} ${err.statusText}`;
    }
    return String(err);
  }
}
