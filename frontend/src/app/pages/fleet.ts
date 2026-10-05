import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../api.service';
import { ConnectorPanel } from './connector-panel';

@Component({
  selector: 'app-fleet',
  imports: [RouterLink, ConnectorPanel],
  template: `
    <div class="page">
      <div class="row" style="margin-bottom: 12px">
        <h1 style="margin: 0">Fleet</h1>
        <span class="muted">
          {{ connectedCount() }}/{{ api.nodes().length }} connected
        </span>
        <span class="spacer"></span>
        <a routerLink="/connect">Add / connect more</a>
        @if (api.nodes().length) {
          <button class="danger" (click)="disconnectAll()">Disconnect all</button>
        }
      </div>

      @if (!api.nodes().length) {
        <div class="card empty">
          No charge points running. <a routerLink="/connect">Connect some</a> to get started.
        </div>
      }

      @for (n of api.nodes(); track n.id) {
        <section class="card">
          <div class="row">
            <h2 style="margin: 0"><a [routerLink]="['/cp', n.id]">{{ n.id }}</a></h2>
            <span class="badge" [class.ok]="n.connected" [class.bad]="!n.connected">
              {{ n.connected ? 'link up' : 'link down' }}
            </span>
            <span class="badge" [class.ok]="n.booted" [class.warn]="!n.booted">
              {{ n.booted ? 'boot accepted' : 'boot pending' }}
            </span>
            @if (n.clock) { <span class="muted small">clock {{ n.clock }}</span> }
            <span class="spacer"></span>
            <button class="small" (click)="cmd(n.id, 'boot')">Boot</button>
            <button class="small" (click)="cmd(n.id, 'heartbeat')">Heartbeat</button>
            <button class="small" (click)="cmd(n.id, 'reconnect')">Reconnect</button>
            <a class="small" [routerLink]="['/cp', n.id]">Details</a>
            <button class="small danger" (click)="remove(n.id)">Disconnect</button>
          </div>

          @for (c of n.connectors; track c.id) {
            <app-connector-panel [nodeId]="n.id" [c]="c" />
          }
        </section>
      }
    </div>
  `,
})
export class FleetPage {
  protected readonly api = inject(ApiService);

  protected connectedCount(): number {
    return this.api.nodes().filter((n) => n.connected).length;
  }

  protected cmd(id: string, action: string): void {
    void this.api.command(`/cp/${encodeURIComponent(id)}/${action}`);
  }

  protected remove(id: string): void { void this.api.disconnect([id]); }
  protected disconnectAll(): void { void this.api.disconnect(); }
}
