import { Routes } from '@angular/router';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { BacktestComponent } from './components/backtest/backtest.component';
import { SectorScreenerComponent } from './components/sector-screener/sector-screener.component';

export const routes: Routes = [
  { path: '', component: DashboardComponent },
  { path: 'backtest', component: BacktestComponent },
  { path: 'sector', component: SectorScreenerComponent },
  { path: '**', redirectTo: '' }
];
