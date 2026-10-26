# Media Catcher

โปรแกรมดาวน์โหลดสื่อ (media downloader) ที่ทำงานร่วมกับเว็บเบราว์เซอร์ สร้างขึ้นเพื่อการศึกษา/ทดลอง (ไม่ใช่เชิงพาณิชย์) โดยใช้แนวทาง **Clean-Room Reimplementation** — ไม่คัดลอกโค้ดจากโปรแกรมปิดอื่น ๆ (เช่น IDM) และไม่เจาะ/bypass DRM

## สถาปัตยกรรม

```
Browser (Extension MV3)  ──Native Messaging──▶  MediaCatcher.NativeHost.exe
                                                        │ Named Pipe IPC
                                                        ▼
                                              MediaCatcher.App.exe (Avalonia)
                                                        │
                                             QueueManager ──▶ EngineRouter
                                                        │
                          ┌─────────────────────────────┼─────────────────────────────┐
                          ▼                             ▼                             ▼
                  HttpDownloadEngine             YtDlpEngine (yt-dlp)        DocumentEngine (Flipbook → PDF)
                  (แบ่ง segment + resume)        (วิดีโอ/สตรีม/แยกเสียง)      (แยกหน้า + รวม PDF)
```

- **Extension** (`extension/`): Manifest V3, ดักจับ media ผ่าน `webRequest` + DOM scan แล้วส่งคำสั่งไปยัง Native Host
- **NativeHost** (`src/MediaCatcher.NativeHost`): สะพาน Chrome/Edge Native Messaging → Named Pipe
- **App** (`src/MediaCatcher.App`): UI Avalonia + tray + จัดการคิว/ตั้งค่า
- **Core** (`src/MediaCatcher.Core`): engine ดาวน์โหลด, คิว, เก็บข้อมูล SQLite, การเชื่อมต่อ IPC
- **Contracts** (`src/MediaCatcher.Contracts`): โปรโตคอล IPC และ DTO ที่ทุกโปรเจกต์ใช้ร่วมกัน

## ความต้องการ (Prerequisites)

- Windows 10/11
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned ที่ `10.0.402` ดู `global.json`)
- `yt-dlp.exe` และ `ffmpeg.exe` (จำเป็นสำหรับ engine วิดีโอ/สตรีมและการรวมไฟล์) — ใช้ `scripts/fetch-tools.ps1` โหลดให้อัตโนมัติ
- เบราว์เซอร์ Chrome หรือ Edge (สำหรับโหลด extension)

## การ build

```powershell
dotnet build MediaCatcher.sln
```

## การรันเทสต์

```powershell
dotnet test tests\MediaCatcher.Core.Tests\MediaCatcher.Core.Tests.csproj
```

## การ setup ครั้งแรก

1. โหลดเครื่องมือภายนอก:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\fetch-tools.ps1
   ```

   (จะดาวน์โหลด `yt-dlp.exe`, `ffmpeg.exe`, `ffprobe.exe` ลงโฟลเดอร์ `tools\`)

2. Build แล้วรัน `MediaCatcher.App` อย่างน้อยหนึ่งครั้งเพื่อสร้าง settings/DB

3. โหลด extension:
   - เปิด `chrome://extensions` (หรือ `edge://extensions`)
   - เปิด **Developer mode**
   - **Load unpacked** แล้วเลือกโฟลเดอร์ `extension\`
   - จด **Extension ID** ที่แสดง

4. ลงทะเบียน Native Messaging Host:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -ExtensionId "<EXTENSION_ID>"
   ```

5. ปิด/เปิดเบราว์เซอร์ใหม่ แล้วเปิด popup ของ extension เพื่อทดสอบ

## โครงสร้างโปรเจกต์

```
MediaCatcher.sln
Directory.Build.props / Directory.Packages.props   # การตั้งค่า build + central package versions
global.json                                         # pin .NET SDK
extension/                                          # Extension MV3 (Chrome/Edge)
  manifest.json
  background/service-worker.js                      # webRequest sniff + native messaging
  content/media-sniffer.js                          # DOM/performance scan
  popup/                                            # UI ป๊อปอัป
  icons/
scripts/
  fetch-tools.ps1                                   # โหลด yt-dlp + ffmpeg
  register-native-host.ps1                          # ลงทะเบียน Native Messaging Host
  generate-icons.ps1 / generate-resources.ps1
src/
  MediaCatcher.Contracts/                           # Protocol + IPC + DTO
  MediaCatcher.Core/
    Http/        # ดาวน์โหลดแบบแบ่ง segment + resume + จำกัดความเร็ว
    YtDlp/       # ห่อ yt-dlp + ffmpeg (analyze/download/progress)
    Document/    # Flipbook → PDF (FlipbookParser, PngDecoder, PdfAssembler, DocumentEngine)
    Queue/       # QueueManager (scheduler, concurrency, per-host limit)
    Routing/     # EngineRouter (เลือก engine ตาม URL)
    Storage/     # SQLite, settings, cookie vault (DPAPI)
    Ipc/         # IpcRequestHandler
  MediaCatcher.NativeHost/                          # Native Messaging Host
  MediaCatcher.App/                                 # UI Avalonia + tray
tests/
  MediaCatcher.Core.Tests/                          # unit tests (routing/parser/PDF/PNG/filename)
```

## Engine ที่รองรับ

| Engine | ใช้กับ | หมายเหตุ |
| :--- | :--- | :--- |
| `Http` | ไฟล์ตรง (mp4/mp3/pdf/zip…) | แบ่ง connection หลายเส้น + resume |
| `YtDlp` | YouTube/Vimeo/สตรีม `.m3u8`/`.mpd` ฯลฯ | ต้องมี `yt-dlp` + `ffmpeg` |
| `Document` | Flipbook (anyflip.com, fliphtml5.com ฯลฯ) | แยกหน้าภาพแล้วรวมเป็น PDF |

การเลือก engine เป็นอัตโนมัติผ่าน `EngineRouter` (ดู host list ใน `src/MediaCatcher.Core/Routing/EngineRouter.cs`)

## หมายเหตุด้านกฎหมาย/จริยธรรม

- ใช้เพื่อ**การศึกษา/ทดลอง**เท่านั้น ไม่ใช่เชิงพาณิชย์
- ไม่คัดลอกโค้ดจากโปรแกรมปิด (IDM) และไม่วิเคราะห์/decompile ไฟล์ crack
- ไม่ bypass DRM (เนื้อหา Widevine จะถูกปฏิเสธด้วย error `DRM_UNSUPPORTED`)
- ดาวน์โหลดเฉพาะเนื้อหาที่เจ้าของอนุญาตให้ดาวน์โหลดเท่านั้น
