import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ScanResponse, HistoricalChartResponse, WatchlistItem, SyncStatus, PortfolioRequest, PortfolioSimulationResult, MultiStrategySimulationResult, SectorRotationResult } from '../models/scanner.model';

@Injectable({
  providedIn: 'root'
})
export class ScannerService {
  private apiUrl = 'http://localhost:5150/api';

  constructor(private http: HttpClient) { }

  scan(strategyName?: string): Observable<ScanResponse> {
    const url = strategyName ? `${this.apiUrl}/scan?strategyName=${strategyName}` : `${this.apiUrl}/scan`;
    return this.http.get<ScanResponse>(url);
  }

  getStrategies(): Observable<string[]> {
    return this.http.get<string[]>(`${this.apiUrl}/scanner/strategies`);
  }

  startSync(): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/sync`, {});
  }

  getSyncStatus(): Observable<SyncStatus> {
    return this.http.get<SyncStatus>(`${this.apiUrl}/sync/status`);
  }

  getChartData(ticker: string, limit?: number): Observable<HistoricalChartResponse> {
    const url = limit ? `${this.apiUrl}/chart/${ticker}?limit=${limit}` : `${this.apiUrl}/chart/${ticker}`;
    return this.http.get<HistoricalChartResponse>(url);
  }

  runPortfolioSimulation(request: PortfolioRequest): Observable<PortfolioSimulationResult> {
    return this.http.post<PortfolioSimulationResult>(`${this.apiUrl}/backtest/portfolio`, request);
  }

  compareAllStrategies(request: PortfolioRequest): Observable<MultiStrategySimulationResult> {
    return this.http.post<MultiStrategySimulationResult>(`${this.apiUrl}/backtest/compare-all`, request);
  }

  getWatchlist(): Observable<WatchlistItem[]> {
    return this.http.get<WatchlistItem[]>(`${this.apiUrl}/watchlist`);
  }

  addToWatchlist(ticker: string, entryPrice: number): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/watchlist`, { ticker, entryPrice });
  }

  removeFromWatchlist(ticker: string): Observable<any> {
    return this.http.delete<any>(`${this.apiUrl}/watchlist/${ticker}`);
  }

  getFyersLoginUrl(): Observable<{ loginUrl: string }> {
    return this.http.get<{ loginUrl: string }>(`${this.apiUrl}/fyers/login`);
  }

  getFyersStatus(): Observable<{ needsLogin: boolean }> {
    return this.http.get<{ needsLogin: boolean }>(`${this.apiUrl}/fyers/status`);
  }

  getFyersOptions(ticker: string): Observable<any> {
    return this.http.get<any>(`${this.apiUrl}/fyers/options/${ticker}`);
  }

  // Sector screener API
  syncSectors(): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/sector/sync`, {});
  }

  getSectorRotation(): Observable<SectorRotationResult> {
    return this.http.get<SectorRotationResult>(`${this.apiUrl}/sector/rotation`);
  }

  runRotationBacktest(request: any): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/sector/rotation-backtest`, request);
  }
}