# Dwell

[Русский](#русский) · [English](#english) · [Releases](https://github.com/zephrx0/Dwell/releases)

<img src="docs/images/dwell.png" alt="Обзор экранного времени в Dwell" width="600">

## Русский

Dwell учитывает экранное время и фоновую активность приложений на Windows. Статистика хранится локально. Приложение работает офлайн и не собирает телеметрию.

- Графики по дням и неделям, категории и история с экспортом в CSV.
- Исключение приложений из статистики через обзор и возврат через настройки.
- Дневные лимиты, расписание отдыха и напоминания о перерывах.
- Тихий автозапуск в трей, системная тема и русский интерфейс.
- Фоновая проверка обновлений, уведомления Windows и установка из настроек.

### Запуск

Скачай и запусти [Dwell-Setup.exe](https://github.com/zephrx0/Dwell/releases/latest/download/Dwell-Setup.exe). Требуется Windows 10 1809+ или Windows 11 x64. .NET уже включён. Следующие версии можно установить в разделе «Настройки → Обновления».

## English

Dwell tracks screen time and background app activity on Windows. All usage data stays on your PC. Dwell works offline and collects no telemetry.

- Daily and weekly charts, categories, and usage history with CSV export.
- Exclude apps from statistics in Overview and restore them in Settings.
- Daily limits, downtime schedules, and break reminders.
- Quiet startup in the system tray, system theme, and English UI.
- Background update checks, Windows notifications, and installation from Settings.

### Run

Download and run [Dwell-Setup.exe](https://github.com/zephrx0/Dwell/releases/latest/download/Dwell-Setup.exe) on Windows 10 1809+ or Windows 11 x64. The .NET runtime is included. Install later versions in Settings → Updates.

## Build

.NET 10 SDK · C# · WinUI 3 · SQLite · Inno Setup 6.3+

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

The script builds and tests Dwell and creates `Dwell-Setup.exe` in the versioned `artifacts` directory.

Developer · [zephrx0](https://github.com/zephrx0)
