import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';

@Component({
  selector: 'app-backtest',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './backtest.component.html'
})
export class BacktestComponent {
  mode: 'single' | 'bulk' = 'single';
  ticker: string = 'HDFCBANK.NS';
  stopLossPct: number = 5.0;
  targetPct: number = 15.0;
  useDynamicExits: boolean = false;
  isSimulating: boolean = false;
  result: any = null;
  bulkResult: any = null;
  selectedStrategy: any = null;

  constructor(private http: HttpClient) {}

  switchMode(newMode: 'single' | 'bulk') {
    this.mode = newMode;
  }

  runBacktest() {
    this.isSimulating = true;
    this.http.post('http://localhost:5150/api/backtest/run', {
      ticker: this.ticker.toUpperCase(),
      stopLossPct: this.stopLossPct / 100.0,
      targetPct: this.targetPct / 100.0,
      useDynamicExits: this.useDynamicExits
    }).subscribe({
      next: (data) => {
        this.result = data;
        this.isSimulating = false;
      },
      error: (err) => {
        console.error(err);
        this.isSimulating = false;
      }
    });
  }

  runBulkBacktest() {
    this.isSimulating = true;
    this.http.post('http://localhost:5150/api/backtest/run-all', {
      stopLossPct: this.stopLossPct / 100.0,
      targetPct: this.targetPct / 100.0,
      useDynamicExits: this.useDynamicExits
    }).subscribe({
      next: (data: any) => {
        this.bulkResult = data;
        if (data && data.strategies && data.strategies.length > 0) {
          this.selectedStrategy = data.strategies[0];
        }
        this.isSimulating = false;
      },
      error: (err) => {
        console.error(err);
        this.isSimulating = false;
      }
    });
  }

  selectStrategy(strategy: any) {
    this.selectedStrategy = strategy;
  }
}
