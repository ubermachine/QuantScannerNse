import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ScannerService } from '../../services/scanner.service';
import { SectorRotationResult, SectorRRGPoint } from '../../models/scanner.model';

@Component({
  selector: 'app-sector-screener',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './sector-screener.component.html'
})
export class SectorScreenerComponent implements OnInit {
  rotationResult: SectorRotationResult | null = null;
  isLoading = false;
  isSyncing = false;
  syncMessage = '';
  error = '';

  // Quadrant colors
  quadColors: Record<string, string> = {
    'Leading': 'emerald',
    'Weakening': 'amber',
    'Lagging': 'rose',
    'Improving': 'blue'
  };

  constructor(private scannerService: ScannerService) {}

  ngOnInit() {
    this.loadRotation();
  }

  loadRotation() {
    this.isLoading = true;
    this.error = '';
    this.scannerService.getSectorRotation().subscribe({
      next: (res) => {
        this.rotationResult = res;
        this.isLoading = false;
      },
      error: () => {
        this.error = 'No sector data synced yet. Click "Sync Sector Data" first.';
        this.isLoading = false;
      }
    });
  }

  syncData() {
    this.isSyncing = true;
    this.syncMessage = '';
    this.scannerService.syncSectors().subscribe({
      next: (res) => {
        this.syncMessage = res.message || 'Sync complete';
        this.isSyncing = false;
        this.loadRotation();
      },
      error: (err) => {
        this.syncMessage = 'Sync failed: ' + (err.error?.error || err.message);
        this.isSyncing = false;
      }
    });
  }

  get sectors() { return this.rotationResult?.sectors || []; }
  get leaders() { return this.sectors.filter(s => s.quadrant === 'Leading').sort((a, b) => b.rsMomentum - a.rsMomentum); }
  get gaining() { return this.sectors.filter(s => s.quadrant === 'Improving').sort((a, b) => b.rsMomentum - a.rsMomentum); }
  get losing() { return this.sectors.filter(s => s.quadrant === 'Weakening').sort((a, b) => a.rsMomentum - b.rsMomentum); }
  get lagging() { return this.sectors.filter(s => s.quadrant === 'Lagging').sort((a, b) => a.rsMomentum - b.rsMomentum); }

  getQuadColor(q: string): string {
    switch(q) {
      case 'Leading': return 'text-emerald-400';
      case 'Weakening': return 'text-amber-400';
      case 'Lagging': return 'text-rose-400';
      case 'Improving': return 'text-blue-400';
      default: return 'text-slate-400';
    }
  }

  getBgColor(q: string): string {
    switch(q) {
      case 'Leading': return 'bg-emerald-500/10 border-emerald-500/30';
      case 'Weakening': return 'bg-amber-500/10 border-amber-500/30';
      case 'Lagging': return 'bg-rose-500/10 border-rose-500/30';
      case 'Improving': return 'bg-blue-500/10 border-blue-500/30';
      default: return 'bg-slate-500/10 border-slate-500/30';
    }
  }
}
