import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ScanResponse, HistoricalChartResponse, WatchlistItem, SyncStatus } from '../models/scanner.model';

@Injectable({
  providedIn: 'root'
})
export class ScannerService {
  private apiUrl = 'http://localhost:5150/api';

  constructor(private http: HttpClient) { }

  scan(): Observable<ScanResponse> {
    return this.http.get<ScanResponse>(`${this.apiUrl}/scan`);
  }

  startSync(): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/sync`, {});
  }

  getSyncStatus(): Observable<SyncStatus> {
    return this.http.get<SyncStatus>(`${this.apiUrl}/sync/status`);
  }

  getChartData(ticker: string): Observable<HistoricalChartResponse> {
    return this.http.get<HistoricalChartResponse>(`${this.apiUrl}/chart/${ticker}`);
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
}
