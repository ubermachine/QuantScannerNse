import { Component } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { NgClass } from '@angular/common';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, NgClass],
  template: `
    <div class="min-h-screen bg-[#080c14] text-slate-100 flex flex-col">
      <nav class="bg-[#090d16] border-b border-slate-900 px-6 py-4 flex space-x-6 items-center shadow-lg">
        <div class="h-8 w-8 rounded bg-gradient-to-tr from-blue-600 to-emerald-500 mr-4"></div>
        <a routerLink="/dashboard" routerLinkActive="text-blue-400 border-b-2 border-blue-400" class="pb-1 font-semibold text-slate-300 hover:text-blue-300">Scanner</a>
        <a routerLink="/backtest" routerLinkActive="text-blue-400 border-b-2 border-blue-400" class="pb-1 font-semibold text-slate-300 hover:text-blue-300">Backtest</a>
      </nav>
      <main class="flex-1 flex flex-col overflow-hidden">
        <router-outlet></router-outlet>
      </main>
    </div>
  `
})
export class AppComponent {
  title = 'frontend';
}
