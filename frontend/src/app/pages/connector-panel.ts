import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ConnectorState, FAULT_CODES } from '../api.service';

/** Live state and every REPL command for one connector. */
@Component({
  selector: 'app-connector-panel',
  imports: [FormsModule, DecimalPipe],
  template: `
    @let s = c();
    <div class="conn">
      <div class="row head">
        <strong>Connector {{ s.id }}</strong>
        <span class="badge" [class.ok]="statusClass() === 'ok'" [class.warn]="statusClass() === 'warn'"
              [class.bad]="statusClass() === 'bad'">{{ s.status }}</span>
        @if (s.errorCode !== 'NoError') { <span class="badge bad" [title]="s.errorInfo">{{ s.errorCode }}</span> }
        @if (s.plugged) { <span class="badge">cable in</span> }
        @if (!s.operative) { <span class="badge warn">inoperative</span> }
        @if (s.reservationId !== null) { <span class="badge warn">reserved #{{ s.reservationId }}</span> }
      </div>

      <div class="meter small muted">
        {{ s.meter.energyWh }} Wh &middot; offering {{ s.meter.powerOfferedW | number: '1.0-0' }} W
        &middot; drawing {{ s.meter.activePowerW | number: '1.0-0' }} W &middot; SoC {{ s.meter.soc | number: '1.0-0' }}%
        @if (s.transaction; as tx) {
          <br />
          transaction {{ tx.id ?? 'pending id' }} &middot; tag {{ tx.idTag }} &middot; {{ tx.energyWh }} Wh
          {{ tx.active ? '' : '(stopped)' }}
        }
      </div>

      <div class="row act">
        <button class="small" (click)="run('plug')" [disabled]="s.plugged">Plug in</button>
        <button class="small" (click)="run('unplug')" [disabled]="!s.plugged">Unplug</button>
        <button class="small" (click)="run('remotestop')" [disabled]="!s.transaction?.active">Stop transaction</button>
        <button class="small" (click)="run('suspendev')">Suspend (EV)</button>
        <button class="small" (click)="run('suspendevse')">Suspend (EVSE)</button>
        <button class="small" (click)="run('resume')">Resume</button>
        <button class="small" (click)="run('status-notification')" title="Send a StatusNotification now">Send status</button>
      </div>

      <div class="row act">
        <input class="narrow" [(ngModel)]="tag" placeholder="idTag" aria-label="idTag" />
        <button class="small" (click)="presentTag()" [disabled]="!tag">Present tag</button>

        <input class="narrow" type="number" min="0" [(ngModel)]="power" aria-label="Offered power in watts" />
        <button class="small" (click)="setPower()">Set power (W)</button>
      </div>

      <div class="row act">
        <select [(ngModel)]="fault" aria-label="Fault code">
          @for (code of faultCodes; track code) { <option [value]="code">{{ code }}</option> }
        </select>
        <button class="small" (click)="raiseFault()">Raise fault</button>
        <button class="small" (click)="run('clear')" [disabled]="s.errorCode === 'NoError'">Clear fault</button>

        <button class="small" (click)="setAvailability(!s.operative)">
          Make {{ s.operative ? 'inoperative' : 'operative' }}
        </button>
      </div>
    </div>
  `,
  styles: `
    .conn { border-top: 1px solid var(--border); padding: 10px 0 2px; }
    .head { margin-bottom: 4px; }
    .act { margin-top: 6px; }
  `,
})
export class ConnectorPanel {
  private readonly api = inject(ApiService);

  readonly nodeId = input.required<string>();
  readonly c = input.required<ConnectorState>();

  protected readonly faultCodes = FAULT_CODES;
  protected tag = 'MOCKTAG01';
  protected power = 7400;
  protected fault: string = FAULT_CODES[0];

  protected readonly statusClass = computed(() => {
    switch (this.c().status) {
      case 'Charging': case 'Available': return 'ok';
      case 'Faulted': case 'Unavailable': return 'bad';
      default: return 'warn';
    }
  });

  private path(action: string): string {
    return `/cp/${encodeURIComponent(this.nodeId())}/connector/${this.c().id}/${action}`;
  }

  protected run(action: string): void { void this.api.command(this.path(action)); }
  protected presentTag(): void { void this.api.command(this.path('tag'), { idTag: this.tag }); }
  protected setPower(): void { void this.api.command(this.path('power'), { watts: Number(this.power) }); }
  protected raiseFault(): void { void this.api.command(this.path('fault'), { code: this.fault }); }
  protected setAvailability(operative: boolean): void {
    void this.api.command(this.path('availability'), { operative });
  }
}
