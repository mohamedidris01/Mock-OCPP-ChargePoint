import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, ConnectRequest } from '../api.service';

type Mode = 'manual' | 'auto' | 'suspendedEv' | 'passive';

interface Form {
  baseUrl: string;
  count: number;
  idPrefix: string;
  first: number;
  connectors: number;
  mode: Mode;
  replace: boolean;
  vendor: string;
  model: string;
  firmware: string;
  powerW: number;
  heartbeatInterval: number;
  meterInterval: number;
  speed: number;
  user: string;
  password: string;
  tag: string;
}

const STORAGE_KEY = 'mock-ocpp.connect-form';

const DEFAULTS: Form = {
  baseUrl: 'ws://127.0.0.1:8887/ocpp',
  count: 1,
  idPrefix: 'CP',
  first: 1,
  connectors: 1,
  mode: 'manual',
  replace: false,
  vendor: 'MockOCPP',
  model: 'CS-1',
  firmware: '1.0.0-mock',
  powerW: 7400,
  heartbeatInterval: 300,
  meterInterval: 30,
  speed: 1,
  user: '',
  password: '',
  tag: 'MOCKTAG01',
};

function loadForm(): Form {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    // The password is never persisted.
    if (saved) return { ...DEFAULTS, ...(JSON.parse(saved) as Partial<Form>), password: '' };
  } catch { /* storage unavailable */ }
  return { ...DEFAULTS };
}

@Component({
  selector: 'app-connect',
  imports: [FormsModule],
  template: `
    <div class="page">
      <h1>Connect charge points</h1>

      <form class="card" (ngSubmit)="submit()" #f="ngForm">
        <div class="grid">
          <label class="field" style="grid-column: span 2">CSMS base URL (the charge point id is appended)
            <input name="baseUrl" [(ngModel)]="form.baseUrl" required placeholder="ws://host:port/ocpp" />
          </label>
          <label class="field">Number of charge points
            <input name="count" type="number" min="1" max="100" [(ngModel)]="form.count" required />
          </label>
          <label class="field">Connectors each
            <input name="connectors" type="number" min="1" max="16" [(ngModel)]="form.connectors" required />
          </label>
          <label class="field">Id prefix
            <input name="idPrefix" [(ngModel)]="form.idPrefix" />
          </label>
          <label class="field">First number
            <input name="first" type="number" min="0" [(ngModel)]="form.first" />
          </label>
          <label class="field">Behaviour
            <select name="mode" [(ngModel)]="form.mode">
              <option value="manual">Manual - I drive it from the UI</option>
              <option value="auto">Auto - cycle charging sessions</option>
              <option value="suspendedEv">Auto, parking in SuspendedEV</option>
              <option value="passive">Passive - boot and heartbeat only</option>
            </select>
          </label>
        </div>

        <p class="muted small">
          Preview: {{ preview() }}
        </p>

        <details>
          <summary>Advanced</summary>
          <div class="grid" style="margin-top: 10px">
            <label class="field">Vendor <input name="vendor" [(ngModel)]="form.vendor" /></label>
            <label class="field">Model <input name="model" [(ngModel)]="form.model" /></label>
            <label class="field">Firmware <input name="firmware" [(ngModel)]="form.firmware" /></label>
            <label class="field">Power per connector (W)
              <input name="powerW" type="number" min="0" [(ngModel)]="form.powerW" /></label>
            <label class="field">Heartbeat interval (s)
              <input name="hb" type="number" min="1" [(ngModel)]="form.heartbeatInterval" /></label>
            <label class="field">Meter interval (s)
              <input name="mi" type="number" min="1" [(ngModel)]="form.meterInterval" /></label>
            <label class="field">Time speed (x)
              <input name="speed" type="number" min="0.1" step="0.1" [(ngModel)]="form.speed" /></label>
            <label class="field">Auto-session idTag <input name="tag" [(ngModel)]="form.tag" /></label>
            <label class="field">Basic auth user <input name="user" autocomplete="off" [(ngModel)]="form.user" /></label>
            <label class="field">Basic auth password
              <input name="password" type="password" autocomplete="off" [(ngModel)]="form.password" /></label>
          </div>
        </details>

        <div class="row" style="margin-top: 14px">
          <button class="primary" type="submit" [disabled]="busy() || f.invalid">
            {{ busy() ? 'Connecting...' : 'Connect' }}
          </button>
          <label class="check small">
            <input type="checkbox" name="replace" [(ngModel)]="form.replace" />
            Replace the running fleet
          </label>
          <span class="spacer"></span>
          @if (api.nodes().length) {
            <span class="muted">{{ api.nodes().length }} running</span>
            <button type="button" class="danger" (click)="disconnectAll()">Disconnect all</button>
          }
        </div>
      </form>
    </div>
  `,
})
export class ConnectPage {
  protected readonly api = inject(ApiService);
  private readonly router = inject(Router);

  protected form: Form = loadForm();
  protected readonly busy = signal(false);

  protected preview(): string {
    const f = this.form;
    const count = Math.max(1, Number(f.count) || 1);
    const first = Math.max(0, Number(f.first) || 0);
    const width = Math.max(4, String(first + count - 1).length);
    const id = (n: number) => f.idPrefix + String(n).padStart(width, '0');
    return count === 1 ? id(first) : `${id(first)} ... ${id(first + count - 1)} (${count} units)`;
  }

  protected async submit(): Promise<void> {
    const f = this.form;
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...f, password: '' })); } catch { /* ignore */ }

    const req: ConnectRequest = {
      baseUrl: f.baseUrl,
      count: Number(f.count),
      idPrefix: f.idPrefix,
      first: Number(f.first),
      connectors: Number(f.connectors),
      powerW: Number(f.powerW),
      vendor: f.vendor,
      model: f.model,
      firmware: f.firmware,
      heartbeatInterval: Number(f.heartbeatInterval),
      meterInterval: Number(f.meterInterval),
      speed: Number(f.speed),
      user: f.user || undefined,
      password: f.user ? f.password : undefined,
      tag: f.tag,
      auto: f.mode === 'auto',
      suspendedEv: f.mode === 'suspendedEv',
      passive: f.mode === 'passive',
      replace: f.replace,
    };

    this.busy.set(true);
    const res = await this.api.connect(req);
    this.busy.set(false);
    if (res) {
      this.api.toast('ok', `Started ${res.added.length} charge point(s)`);
      await this.router.navigateByUrl('/fleet');
    }
  }

  protected async disconnectAll(): Promise<void> {
    const res = await this.api.disconnect();
    if (res) this.api.toast('ok', `Stopped ${res.removed} charge point(s)`);
  }
}
