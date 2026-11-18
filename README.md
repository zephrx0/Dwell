# Dwell

[Русский](#русский) · [English](#english) · [Releases](https://github.com/zephrx0/Dwell/releases)

<img src="docs/images/dwell.png" alt="Обзор экранного времени в Dwell" width="600">

## Русский

Dwell учитывает экранное время и фоновую активность приложений на Windows. Статистика хранится локально. Приложение работает офлайн и не собирает телеметрию.

- Графики по дням и неделям, категории и история с экспортом в CSV.
- Дневные лимиты, расписание отдыха и напоминания о перерывах.
- Тихий автозапуск в трей, системная тема и русский интерфейс.

### Запуск

Скачай [Dwell.exe](https://github.com/zephrx0/Dwell/releases/latest/download/Dwell.exe) и запусти. Требуется Windows 10 1809+ или Windows 11 x64. .NET уже включён.

## English

Dwell tracks screen time and background app activity on Windows. All usage data stays on your PC. Dwell works offline and collects no telemetry.

- Daily and weekly charts, categories, and usage history with CSV export.
- Daily limits, downtime schedules, and break reminders.
- Quiet startup in the system tray, system theme, and English UI.

### Run

Download [Dwell.exe](https://github.com/zephrx0/Dwell/releases/latest/download/Dwell.exe) and run it on Windows 10 1809+ or Windows 11 x64. The .NET runtime is included.

## Build

.NET 10 SDK · C# · WinUI 3 · SQLite

```powershell
dotnet build
dotnet test tests/Dwell.Core.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -SingleFile
```

Developer · [zephrx0](https://github.com/zephrx0)
