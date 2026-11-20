# AGENTS.md - PlanLite Development Guide

This document defines the architecture, conventions, toolchain commands, and domain invariants for **PlanLite**. Read this before making changes.

## 1. Product & Scope
PlanLite is a lightweight, fully local Windows 10/11 desktop work-plan manager built with **C# / .NET 10 LTS / WPF / WPF-UI / SQLite**.
- Strictly local: **zero network requests**, no telemetry, no login, no Electron/WebView/server.
- Minimal dependencies: `Microsoft.Data.Sqlite`, `WPF-UI`, `WPF-UI.Tray`, `CommunityToolkit.Mvvm` (no EF Core, no heavy DI container).
- See [docs/PRD.md](docs/PRD.md) for the complete product specification.

## 2. Project Architecture
- `PlanLite.sln`
- `src/PlanLite.Core/` (`net10.0`) — UI-independent domain models, week calculations, SQLite schema/repository, JSON settings store, Markdown report generator, backup/restore service, and app path resolution.
- `src/PlanLite/` (`net10.0-windows`, WPF `WinExe`) — Fluent UI views, MVVM ViewModels, system tray integration, bottom-right Notification Center popup window, optional mini floating widget, global hotkey hook, startup registry integration, theme service, and single-instance activation.
- `tests/PlanLite.Core.Tests/` (`net10.0`, xUnit) — Unit and integration tests covering `WeekHelper`, `PlanRepository`, `ReportService`, `SettingsStore`, `AppPaths`, and `BackupService`.
- `scripts/publish.ps1` — Self-contained single-file portable Windows x64 build & publish script.

## 3. Toolchain & CLI Usage (.NET 10 LTS)
Requires the **.NET 10 SDK** on Windows x64:

```powershell
dotnet build PlanLite.sln -c Release
dotnet test tests/PlanLite.Core.Tests/PlanLite.Core.Tests.csproj -c Release --no-build
```

If the machine has multiple SDKs and global `PATH` resolves `dotnet` to an older SDK while .NET 10 is installed under `$env:LOCALAPPDATA\Microsoft\dotnet`, invoke it explicitly:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"

& $dotnet build PlanLite.sln -c Release
& $dotnet test tests/PlanLite.Core.Tests/PlanLite.Core.Tests.csproj -c Release --no-build
```

For self-contained single-file release publishing:
```powershell
.\scripts\publish.ps1
```

## 4. Data Storage & Portable Mode
Resolved by `AppPaths`:
- **Default mode** (program/data separated, upgrade-safe): `%LocalAppData%\PlanLite`
- **Portable mode**: if a `portable.flag` file exists in `AppContext.BaseDirectory`, switches to `<AppContext.BaseDirectory>\data\`.
- Files inside `<DataDirectory>`:
  - `planlite.db` (SQLite database in WAL mode)
  - `settings.json` (JSON user settings, written atomically via temp file + move)
  - `logs\error.log` (unhandled exception log, created only if errors occur)

## 5. SQLite Schema & Domain Invariants
1. **Progress ↔ Status Coupling**:
   - `Progress` is always clamped to `[0, 100]`.
   - `0` → `PlanTaskStatus.NotStarted` (0, 未开始)
   - `1..99` → `PlanTaskStatus.InProgress` (1, 进行中)
   - `100` → `PlanTaskStatus.Completed` (2, 已完成)
   - `CompletedAtUtc` is populated when `Progress == 100` and cleared when `< 100`.
   - `DailyTask.LastNonCompleteProgress` preserves the previous `< 100` progress when quick-toggling completion so unchecking restores prior progress.
2. **Natural Weeks**:
   - Weeks run **Monday to Sunday** (`WeekStart` is always Monday, stored as `yyyy-MM-dd`).
   - Week numbers use ISO-8601 (`System.Globalization.ISOWeek`).
3. **Weekly Progress Modes & Auto Calculation**:
   - `WeeklyProgressMode.Manual (0)`: user sets progress directly.
   - `WeeklyProgressMode.Auto (1)`: equal-weight rounded average (`MidpointRounding.AwayFromZero`) of all `daily_tasks` whose `weekly_task_id` equals this weekly task's `id` (`0` if none linked).
   - Any daily task create, progress update, relink, delete, or weekly mode switch to `Auto` must recalculate affected weekly tasks atomically inside the same SQLite transaction.
   - Deleting a weekly task sets linked daily tasks' `weekly_task_id = NULL` (`ON DELETE SET NULL`); daily tasks are preserved.
4. **Non-Destructive & Idempotent Carry-Over**:
   - Carrying over unfinished tasks (`Progress < 100`) creates new rows in the target period with `carried_from_id` pointing to the source task ID and **never modifies or deletes** source tasks.
   - Re-running carry-over into the same target period skips any source task already referenced by `carried_from_id` in the target period.
   - Cross-week daily carry-over maps `weekly_task_id` to the carried weekly task in the target week if present; otherwise clears `weekly_task_id` (`NULL`) so historical week auto-progress is never altered.
5. **Backup & Restore**:
   - Backups use `SqliteConnection.BackupDatabase` into a `.zip` archive containing `planlite.db`, `settings.json` (if present), and `manifest.json`.
   - Restore validates archive integrity (`PRAGMA quick_check` + table checks), keeps a `planlite.pre-restore.db` safety copy, calls `SqliteConnection.ClearAllPools()`, removes stale `-wal`/`-shm` files, and replaces files safely.

## 6. Performance, Tray & UI Conventions
- **Zero High-Frequency Polling**: No background polling timers. Midnight rollover uses a single exact `DispatcherTimer` set to next local midnight plus `SystemEvents.TimeChanged` / `SystemEvents.PowerModeChanged` hooks (all unsubscribed on exit).
- **Tray Behavior**:
  - App starts hidden in the Windows system tray by default (pass `--show` to open on launch).
  - Left-click toggles `MainWindow` at bottom-right of the active monitor's DPI-scaled `WorkArea` with a 250ms deactivation debounce so clicking the tray icon while open cleanly hides without reopening.
  - `MainWindow.Deactivated` auto-hides when `!IsPinned` and no modal/file dialog (`_suppressAutoHide`) is open, flushing any debounced slider edits.
  - Inline task editor lives as an overlay inside `MainWindow` so editing does not trigger `Deactivated`.
- **Single Instance**: Enforced via `Local\PlanLite_SingleInstance_Mutex` + `PlanLite_Activate_Pipe` named pipe.
- **Icons & Assets**: Embedded as WPF `<Resource>` (`pack://application:,,,/Assets/PlanLite.ico`) so single-file self-contained EXE runs with zero external asset files.
