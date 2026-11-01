# Every Scanner

A Windows desktop app for scanning paper documents into clean, searchable PDFs. It talks to your scanner, straightens and cleans every page automatically, lets you touch pages up, and saves the result into a folder for each day.

Built with WinUI 3 (Windows App SDK) on .NET 8. Image processing is done offline with OpenCV, so nothing leaves your computer.

## Features

### Scanning
- Works with flatbed and document-feeder scanners, through WIA and the scanner's own fast TWAIN driver when it has one.
- Source: automatic, flatbed or document feeder. Double-sided (front and back) scanning.
- Colour, grayscale or black and white, at the resolution you choose.
- Scan a whole stack at once, skip blank pages, or use fast scan (lower resolution, no straightening).
- Paper size presets: A4, A5, A6, B5, Letter, Legal, or the whole page.

### Page clean-up
- Automatic crop, deskew and perspective flattening of crooked or photographed pages.
- Smart mode tries several looks on each page, scores how readable each one is, and picks the best.
- Magic Color and a print-friendly look for Persian and English text that whitens the paper and darkens the ink without losing thin strokes or diacritics.
- Manual tools: straighten, crop, a four-corner perspective tool, rotate, split a page in two (for a book scanned open), brightness, contrast, highlights and shadows.
- Brush tools: dodge, burn and erase, for removing marks and stains.
- Apply one page's look to every page, or reset a page's edits.

### Output
- Save as PDF or export JPEG. Add pages to an existing PDF, and import images or PDFs into a batch.
- OCR makes PDFs searchable, in Persian and English, using the Windows OCR engines.
- A folder for each day, named with a Shamsi date (for example `1405-08-05`) or a Gregorian date (`2026-10-27`).
- A search box over saved scans, and a button to show a file in its folder.

### Interface
- Light, dark or system theme (`Ctrl+T` cycles them).
- Keyboard shortcuts (press `F1` for the list).
- Saved workspace, so an unfinished batch is still there when you reopen the app.

## Requirements

- Windows 10 (build 17763) or later, 64-bit.
- A scanner with a WIA or TWAIN driver installed.
- To build: the .NET 8 SDK and, for the installer, [Inno Setup](https://jrsoftware.org/isinfo.php).

## Build and run

```powershell
dotnet build SScanner.csproj -c Release
dotnet run --project SScanner.csproj
```

The app is self-contained, so end users do not need to install .NET or the Windows App SDK.

## Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

This publishes a self-contained build to `publish\` and compiles `installer\SScanner.iss` with Inno Setup. The installer is written to `installer\Output\`. It installs for the current user and does not need administrator rights.

## Project layout

| Path | What it holds |
|------|---------------|
| `MainWindow.xaml`, `MainWindow*.cs` | The main window and its logic |
| `CropOverlay.cs`, `QuadOverlay.cs`, `RotateOverlay.cs`, `BrushOverlay.cs` | On-page editing tools |
| `Services/ScannerService.cs`, `Services/TwainScanner.cs` | Scanner access (WIA and TWAIN) |
| `Services/ImageEnhancer.cs` | OpenCV clean-up: crop, deskew, lighting, smart mode, brushes |
| `Services/OcrService.cs` | Searchable PDF text |
| `Services/PdfExporter.cs`, `Services/PdfImporter.cs` | PDF output and import (PDFsharp) |
| `Services/WorkspaceStore.cs`, `Services/ScanIndex.cs`, `Services/DailyFolder.cs` | Saved batches, search and per-day folders |
| `installer/` | Inno Setup script |

## Author

Arman Soltani
