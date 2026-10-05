import { Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { ApiService, AuthEntry, ConfigKey, ProfileRow } from '../api.service';

type Tab = 'config' | 'auth' | 'profiles' | 'reservations' | 'datatransfer' | 'sim';

@Component({
  selector: 'app-detail',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="page">
      <div class="row" style="margin-bottom: 12px">
        <a routerLink="/fleet">&larr; Fleet</a>
        <h1 style="margin: 0">{{ id() }}</h1>
        @if (node(); as n) {
          <span class="badge" [class.ok]="n.connected" [class.bad]="!n.connected">
            {{ n.connected ? 'link up' : 'link down' }}
          </span>
          <span class="badge" [class.ok]="n.booted" [class.warn]="!n.booted">
            {{ n.booted ? 'boot accepted' : 'boot pending' }}
          </span>
        }
      </div>

      @if (!node()) {
        <div class="card empty">No running charge point called {{ id() }}.</div>
      } @else {
        <div class="row tabs">
          @for (t of tabs; track t.key) {
            <button [class.primary]="tab() === t.key" (click)="tab.set(t.key)">{{ t.label }}</button>
          }
        </div>

        <div class="card">
          @switch (tab()) {
            @case ('config') {
              <div class="row" style="margin-bottom: 8px">
                <input [ngModel]="filter()" (ngModelChange)="filter.set($event)" placeholder="filter keys" />
                <button (click)="loadConfig()">Refresh</button>
              </div>
              <table>
                <thead><tr><th>Key</th><th>Value</th><th></th></tr></thead>
                <tbody>
                  @for (k of filteredConfig(); track k.name) {
                    <tr>
                      <td class="mono">{{ k.name }} @if (k.readOnly) { <span class="badge">read-only</span> }
                        @if (k.mutability === 'RebootRequired') { <span class="badge warn">reboot</span> }</td>
                      <td>
                        @if (k.readOnly) { <span class="mono">{{ k.value }}</span> }
                        @else { <input [(ngModel)]="edits[k.name]" [placeholder]="k.value" /> }
                      </td>
                      <td>
                        @if (!k.readOnly) {
                          <button class="small" (click)="saveKey(k)" [disabled]="edits[k.name] === undefined">Set</button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }

            @case ('auth') {
              <p class="muted small">Local Authorization List, version {{ authVersion() }} (set by the CSMS with SendLocalList).</p>
              @if (!authEntries().length) { <div class="empty">The list is empty.</div> }
              @else {
                <table>
                  <thead><tr><th>idTag</th><th>Info</th></tr></thead>
                  <tbody>@for (e of authEntries(); track e.idTag) {
                    <tr><td class="mono">{{ e.idTag }}</td><td class="mono">{{ e.info }}</td></tr>
                  }</tbody>
                </table>
              }
              <button (click)="loadAuth()">Refresh</button>
            }

            @case ('profiles') {
              <p class="muted small">Charging profiles installed by the CSMS (SetChargingProfile).</p>
              @if (!profiles().length) { <div class="empty">No charging profiles installed.</div> }
              @else {
                <table>
                  <thead><tr><th>Connector</th><th>Id</th><th>Stack</th><th>Purpose</th><th>Kind</th><th>Transaction</th><th>Raw</th></tr></thead>
                  <tbody>@for (p of profiles(); track p.connectorId + '-' + p.id) {
                    <tr>
                      <td>{{ p.connectorId }}</td><td>{{ p.id }}</td><td>{{ p.stackLevel }}</td>
                      <td>{{ p.purpose }}</td><td>{{ p.kind }}</td><td>{{ p.transactionId ?? '-' }}</td>
                      <td><details><summary>json</summary><pre>{{ pretty(p.raw) }}</pre></details></td>
                    </tr>
                  }</tbody>
                </table>
              }
              <button (click)="loadProfiles()">Refresh</button>
            }

            @case ('reservations') {
              <p class="muted small">Reservations made by the CSMS (ReserveNow).</p>
              @if (!reservations().length) { <div class="empty">No active reservations.</div> }
              @else {
                <table>
                  <thead><tr><th>Connector</th><th>Reservation</th><th>For tag</th><th>Expires</th></tr></thead>
                  <tbody>@for (c of reservations(); track c.id) {
                    <tr><td>{{ c.id }}</td><td>#{{ c.reservationId }}</td>
                      <td class="mono">{{ c.reservedForTag }}</td><td>{{ c.reservationExpiry }}</td></tr>
                  }</tbody>
                </table>
              }
            }

            @case ('datatransfer') {
              <div class="grid">
                <label class="field">Vendor id <input [(ngModel)]="dt.vendorId" /></label>
                <label class="field">Message id (optional) <input [(ngModel)]="dt.messageId" /></label>
                <label class="field" style="grid-column: 1 / -1">Data (optional)
                  <textarea rows="3" [(ngModel)]="dt.data"></textarea></label>
              </div>
              <div class="row" style="margin-top: 10px">
                <button class="primary" (click)="sendDataTransfer()" [disabled]="!dt.vendorId">Send DataTransfer</button>
              </div>
              @if (dtReply() !== null) {
                <h3 style="margin-top: 12px">Reply</h3>
                <pre>{{ dtReply() }}</pre>
              }
            }

            @case ('sim') {
              <div class="row">
                <label class="field">Meter time scale (x)
                  <input type="number" min="0.1" step="0.1" [(ngModel)]="speed" /></label>
                <button (click)="applySpeed()">Apply</button>
              </div>
              <p class="muted small">Compresses simulated time: 10 makes the meters run ten times faster.</p>
            }
          }
        </div>
      }
    </div>
  `,
  styles: `.tabs { margin-bottom: 12px; }`,
})
export class DetailPage {
  private readonly api = inject(ApiService);

  protected readonly id = toSignal(inject(ActivatedRoute).paramMap.pipe(map((p) => p.get('id') ?? '')), { initialValue: '' });
  protected readonly node = computed(() => this.api.nodes().find((n) => n.id === this.id()));

  protected readonly tabs: { key: Tab; label: string }[] = [
    { key: 'config', label: 'Configuration' },
    { key: 'auth', label: 'Auth list' },
    { key: 'profiles', label: 'Charging profiles' },
    { key: 'reservations', label: 'Reservations' },
    { key: 'datatransfer', label: 'DataTransfer' },
    { key: 'sim', label: 'Simulation' },
  ];
  protected readonly tab = signal<Tab>('config');

  protected readonly filter = signal('');
  protected readonly config = signal<ConfigKey[]>([]);
  protected edits: Record<string, string> = {};
  protected readonly filteredConfig = computed(() => {
    const f = this.filter().trim().toLowerCase();
    return f ? this.config().filter((k) => k.name.toLowerCase().includes(f)) : this.config();
  });

  protected readonly authVersion = signal(0);
  protected readonly authEntries = signal<AuthEntry[]>([]);
  protected readonly profiles = signal<ProfileRow[]>([]);
  protected readonly reservations = computed(() =>
    (this.node()?.connectors ?? []).filter((c) => c.reservationId !== null));

  protected dt = { vendorId: '', messageId: '', data: '' };
  protected readonly dtReply = signal<string | null>(null);
  protected speed = 1;

  constructor() {
    // Load a tab's data when it is opened (and when the charge point changes).
    effect(() => {
      const id = this.id();
      const tab = this.tab();
      if (!id) return;
      if (tab === 'config') void this.loadConfig();
      if (tab === 'auth') void this.loadAuth();
      if (tab === 'profiles') void this.loadProfiles();
    });
  }

  private base(): string { return `/cp/${encodeURIComponent(this.id())}`; }

  protected async loadConfig(): Promise<void> {
    this.config.set((await this.api.get<ConfigKey[]>(`${this.base()}/config`)) ?? []);
    this.edits = {};
  }

  protected async saveKey(k: ConfigKey): Promise<void> {
    const result = await this.api.setConfig(this.id(), k.name, this.edits[k.name]);
    if (result === undefined) return;
    this.api.toast(result === 'Accepted' ? 'ok' : 'error', `${k.name}: ${result}`);
    await this.loadConfig();
  }

  protected async loadAuth(): Promise<void> {
    const r = await this.api.get<{ version: number; entries: AuthEntry[] }>(`${this.base()}/authlist`);
    this.authVersion.set(r?.version ?? 0);
    this.authEntries.set(r?.entries ?? []);
  }

  protected async loadProfiles(): Promise<void> {
    this.profiles.set((await this.api.get<ProfileRow[]>(`${this.base()}/profiles`)) ?? []);
  }

  protected async sendDataTransfer(): Promise<void> {
    const r = await this.api.command<{ reply: string | null }>(`${this.base()}/datatransfer`, {
      vendorId: this.dt.vendorId,
      messageId: this.dt.messageId || null,
      data: this.dt.data || null,
    });
    if (r) this.dtReply.set(r.reply ? this.pretty(r.reply) : '(no reply)');
  }

  protected async applySpeed(): Promise<void> {
    await this.api.command(`${this.base()}/speed`, { factor: Number(this.speed) }, `Time scale x${this.speed}`);
  }

  protected pretty(json: string): string {
    try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; }
  }
}
