import { Routes } from '@angular/router';
import { ConnectPage } from './pages/connect';
import { DetailPage } from './pages/detail';
import { FleetPage } from './pages/fleet';
import { LogPage } from './pages/log';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'connect' },
  { path: 'connect', component: ConnectPage, title: 'Connect' },
  { path: 'fleet', component: FleetPage, title: 'Fleet' },
  { path: 'cp/:id', component: DetailPage, title: 'Charge point' },
  { path: 'log', component: LogPage, title: 'Message log' },
  { path: '**', redirectTo: 'connect' },
];
