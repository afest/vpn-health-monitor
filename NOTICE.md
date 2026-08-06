# Сторонние компоненты

VPN Health Monitor распространяется под лицензией MIT — см. [LICENSE](LICENSE).

## В исходниках: ни одного пакета NuGet

Приложение (`VpnHealthMonitor.csproj`) собирается на .NET 8 SDK и Windows Desktop
(WPF/WinForms) без единой сторонней зависимости — список `<PackageReference>` в
проекте пуст.

## В установщике: рантайм .NET 8 внутри

Установщик собирается как **self-contained**: рантайм .NET 8 и библиотеки Windows
Desktop (WPF, WinForms) упакованы внутрь `VpnHealthMonitor.exe`, поэтому мы их
распространяем, а не просто используем.

| Компонент | Правообладатель | Лицензия |
|---|---|---|
| [.NET Runtime](https://github.com/dotnet/runtime) | Microsoft | MIT |
| [Windows Desktop Runtime (WPF, WinForms)](https://github.com/dotnet/wpf) | Microsoft | MIT |

Self-contained-распространение прямо разрешено условиями .NET. Компонентов под GPL
или LGPL в поставке нет — состав проверялся по собранному файлу, а не по списку
зависимостей: поиск маркеров `libx264`, `libx265`, `avcodec`, `ffmpeg`, `libmysql`,
`--enable-gpl` и текста GNU General Public License по байтам `VpnHealthMonitor.exe`
даёт ноль текстовых вхождений.

Установщик собран [Inno Setup 6](https://jrsoftware.org/isinfo.php) (Jordan Russell,
лицензия Inno Setup — свободное использование, включая коммерческое). Код Inno Setup
входит в `VpnHealthMonitor-Setup-*.exe`, но не в само приложение.

## Только для разработки — в поставку не входит

Тест-проект `VpnHealthMonitor.Tests.csproj` (xUnit) подключает три пакета. Они
работают только при запуске тестов и в сборку приложения не попадают:

| Компонент | Лицензия |
|---|---|
| [xunit](https://github.com/xunit/xunit) | MIT |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | MIT |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |

## Графика

Иконка приложения (`VpnHealthMonitor/Heart.ico`) отрисована из примитивов скриптом
[`tools/make-icon.ps1`](tools/make-icon.ps1) — сторонней графики в проекте нет.

---

Если в основной проект добавится рантайм-зависимость, этот файл нужно обновить:
перед выпуском релиза сверяйте `<PackageReference>` в `VpnHealthMonitor.csproj` и
состав `publish\win-x64\` с таблицами выше. Устаревший NOTICE хуже отсутствующего.
