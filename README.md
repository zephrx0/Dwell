# Dwell

[English](#english)

Dwell показывает, на что уходит время за компьютером. В обзоре видно, сколько времени ты провёл в каждом приложении и сколько оно работало в фоне. На графике можно посмотреть день или неделю и увидеть, сколько времени заняла каждая категория. Историю можно выгрузить в CSV.

<img src="docs/images/dwell.png" alt="Обзор экранного времени в Dwell" width="600">

## Установка

Скачай и запусти установщик. Подойдёт Windows 10 версии 1809 или новее, а также Windows 11. Установщик рассчитан на x64 и уже включает .NET. [Скачать Dwell-Setup.exe](https://github.com/zephrx0/Dwell/releases/latest/download/Dwell-Setup.exe)

Когда выйдет новая версия, Dwell покажет уведомление Windows. Установить обновление можно в разделе «Настройки → Обновления». [История релизов](https://github.com/zephrx0/Dwell/releases)

## Как пользоваться

Если в статистику попала фоновая служба, раскрой её строку в обзоре и нажми «Исключить из статистики». Чтобы вернуть приложение вместе с его историей, открой «Скрытые приложения» в боковом меню и нажми «Вернуть в статистику».

В разделе «Лимиты и отдых» можно задать дневной лимит, настроить расписание отдыха или включить напоминания о перерывах. Тему, язык и автозапуск в трее можно поменять в настройках.

Раздел «Энергопотребление» показывает ватты в реальном времени. Пока он открыт, Dwell строит график за последние пять минут. Над графиком можно выбрать общую мощность или отдельный датчик. Dwell читает датчики оборудования и счётчики Windows, в том числе мощность процессора и памяти, если компьютер их передаёт. Источник общей цифры указан над графиком, а в таблице видно, какие показания вошли в расчёт.

История и настройки хранятся на твоём компьютере. Статистика работает офлайн.

## English

Dwell shows where your time goes on Windows. Pick a day or a week in Overview to see how long each app was on screen or running in the background, and how your time breaks down by category. You can also export your history as a CSV.

If a background service is cluttering up your stats, expand its row in Overview and select **Exclude from statistics**. To bring it back with its history, open **Hidden apps** in the sidebar and select **Restore to statistics**.

**Limits and breaks** is where you can set daily limits, schedule downtime, and turn on break reminders. Theme, language, and startup in the system tray are in Settings. Your history and settings stay on your PC, and tracking works offline.

Open **Power usage** for a live chart in watts. You can plot total power or choose a single sensor above the chart. Dwell reads hardware sensors and Windows energy counters, including CPU and memory readings on supported PCs. The label above the chart identifies the source of the total, and the table shows which readings count toward it.

Run the installer on an x64 PC with Windows 10 version 1809 or later, or Windows 11. The .NET runtime is included. Dwell sends a Windows notification when an update is available. You can install it from **Settings → Updates**. [Download Dwell-Setup.exe](https://github.com/zephrx0/Dwell/releases/latest/download/Dwell-Setup.exe)

## Build

Dwell uses C#, WinUI 3, and SQLite. To build the installer, run this from the repository root on Windows with the .NET 10 SDK and Inno Setup 6.3 or newer installed.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

The script builds Dwell, runs the tests, and puts `Dwell-Setup.exe` in a versioned folder under `artifacts`.

Hardware sensors are read through LibreHardwareMonitor. [Hardware sensor licenses](src/Dwell/HardwareSensorNotices.txt)

Made by [zephrx0](https://github.com/zephrx0).
