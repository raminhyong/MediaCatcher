param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\MediaCatcher.App\Resources')
)

$ErrorActionPreference = 'Stop'

$thaiText = @'
AppTitle|Media Catcher
Tagline|ตัวจัดการดาวน์โหลดสื่อ
AddUrl|เพิ่มลิงก์
StartAll|เริ่มทั้งหมด
PauseAll|หยุดทั้งหมด
Pause|หยุด
Resume|ทำต่อ
ResumeAll|ทำต่อทั้งหมด
OK|ตกลง
Remove|ลบ
ClearHistory|ล้างประวัติ
OpenFolder|เปิดโฟลเดอร์
OpenFile|เปิดไฟล์
CopyUrl|คัดลอกลิงก์
Retry|ลองใหม่
Cancel|ยกเลิก
Close|ปิด
Save|บันทึก
Settings|ตั้งค่า
Search|ค้นหา…
All|ทั้งหมด
Video|วิดีโอ
Audio|เสียง
Images|รูปภาพ
Documents|เอกสาร
Archives|ไฟล์บีบอัด
Programs|โปรแกรม
Other|อื่น ๆ
Queue|คิว
Name|ชื่อไฟล์
Size|ขนาด
Progress|ความคืบหน้า
Speed|ความเร็ว
Eta|เวลาที่เหลือ
Status|สถานะ
Source|ที่มา
Details|รายละเอียด
NoSelection|เลือกรายการเพื่อดูรายละเอียด
Ready|พร้อม
Idle|ไม่มีงาน
ActiveDownloads|กำลังดาวน์โหลด
Queued|รอคิว
Probing|กำลังตรวจสอบ
Downloading|กำลังดาวน์โหลด
Merging|กำลังรวมไฟล์
Paused|หยุดชั่วคราว
Failed|ล้มเหลว
Canceled|ยกเลิกแล้ว
WaitingForSchedule|รอเวลา
Completed|เสร็จสิ้น
ClipboardDetected|พบลิงก์ในคลิปบอร์ด
AddToQueue|เพิ่มเข้าคิว
Dismiss|ปิด
DownloadRoot|โฟลเดอร์ดาวน์โหลด
Browse|เลือก…
Connections|จำนวนการเชื่อมต่อ
SpeedLimit|จำกัดความเร็ว (KB/s, 0 = ไม่จำกัด)
MaxConcurrent|ดาวน์โหลดพร้อมกันสูงสุด
PerHostLimit|ต่อโฮสต์สูงสุด
Proxy|พร็อกซี (URL)
UserAgent|User-Agent
CategoryFolders|โฟลเดอร์แยกหมวด
EmbedMetadata|ฝังข้อมูลเมตา
EmbedThumbnail|ฝังภาพปก
WriteSubtitles|ดาวน์โหลดคำบรรยาย
ScheduleEnabled|เปิดใช้ตารางเวลา
ScheduleStart|เริ่ม
ScheduleStop|หยุด
AutoResume|ทำต่ออัตโนมัติเมื่อเปิดแอป
StopWhenQueueEmpty|ปิดแอปเมื่อคิวว่าง
ClipboardMonitor|ตรวจคลิปบอร์ด
StartWithWindows|เริ่มพร้อม Windows
MinimizeToTray|ย่อลงถาด
CloseToTray|ปิดหน้าต่างแล้วซ่อนในถาด
CookiesFromBrowser|ใช้คุกกี้จากเบราว์เซอร์
YtDlpUpdate|อัปเดต yt-dlp
Theme|ธีม
Language|ภาษา
Tools|เครื่องมือ
YtDlp|yt-dlp
Ffmpeg|ffmpeg
ReadyStatus|พร้อมใช้งาน
Missing|ไม่พบ
Test|ทดสอบ
UpdateTools|อัปเดตเครื่องมือ
FetchTools|ดาวน์โหลดเครื่องมือ
HostRegistration|การลงทะเบียน Native Host
RegisterHost|ลงทะเบียน host
TestPing|ทดสอบการเชื่อมต่อ
Extension|ส่วนขยายเบราว์เซอร์
OpenExtensionFolder|เปิดโฟลเดอร์ส่วนขยาย
FirstRunTitle|เริ่มต้นใช้งาน Media Catcher
FirstRunIntro|ตรวจสอบความพร้อมแล้วทำตามขั้นตอน
Refresh|รีเฟรช
Analyze|วิเคราะห์
Analyzing|กำลังวิเคราะห์…
Add|เพิ่ม
AddPlaylist|เพิ่มทั้งเพลย์ลิสต์
Format|รูปแบบ
Preset|คุณภาพ
FileName|ชื่อไฟล์ (ไม่บังคับ)
Category|หมวด
Url|ลิงก์
UrlPlaceholder|วางลิงก์วิดีโอหรือไฟล์
PlaylistItems|รายการในเพลย์ลิสต์
DirectDownload|ดาวน์โหลดโดยตรง
AnalyzeHint|รองรับ YouTube และเว็บที่ yt-dlp รองรับ
Duplicate|มีอยู่ในคิวแล้ว
Added|เพิ่มเข้าคิวแล้ว
Error|ข้อผิดพลาด
DownloadFailed|ดาวน์โหลดล้มเหลว
ToolMissing|ไม่พบเครื่องมือที่จำเป็น
AppNotRunning|แอปยังไม่ทำงาน
HostConnected|เชื่อมต่อแล้ว
HostNotInstalled|ยังไม่ติดตั้ง host
RestartForLanguage|ต้องรีสตาร์ทแอปเพื่อเปลี่ยนภาษา
OpenApp|เปิดแอป
Exit|ออก
ShowWindow|เปิดหน้าต่าง
NoDownloads|ยังไม่มีรายการดาวน์โหลด
AddFirst|กด “เพิ่มลิงก์” เพื่อเริ่ม
Clear|ล้าง
Apply|นำไปใช้
ConfirmClearHistory|ล้างรายการที่เสร็จแล้วทั้งหมด?
SelectFolder|เลือกโฟลเดอร์
InvalidUrl|ลิงก์ไม่ถูกต้อง
UnsupportedUrl|ลิงก์นี้ไม่รองรับ
DrmUnsupported|เนื้อหานี้มีการป้องกัน DRM
ToolStatus|สถานะเครื่องมือ
ExtensionsHint|Chrome ต้องติดตั้งด้วยมือผ่าน chrome://extensions (Developer mode → Load unpacked)
InstallHostHint|กด “ลงทะเบียน host” หรือรัน scripts/install-native-host.ps1
StorageHint|ตั้งค่าและคิวเก็บที่ %APPDATA%\MediaCatcher
DownloadsCount|รายการ
Browser|เบราว์เซอร์
Clipboard|คลิปบอร์ด
Manual|ด้วยตนเอง
Takeover|แย่งดาวน์โหลด
Priority|ลำดับความสำคัญ
OpenSettings|ตั้งค่า
Unknown|ไม่ทราบ
'@

$englishText = @'
AppTitle|Media Catcher
Tagline|Media download manager
AddUrl|Add URL
StartAll|Start all
PauseAll|Pause all
Pause|Pause
Resume|Resume
ResumeAll|Resume all
OK|OK
Remove|Remove
ClearHistory|Clear history
OpenFolder|Open folder
OpenFile|Open file
CopyUrl|Copy URL
Retry|Retry
Cancel|Cancel
Close|Close
Save|Save
Settings|Settings
Search|Search…
All|All
Video|Video
Audio|Audio
Images|Images
Documents|Documents
Archives|Archives
Programs|Programs
Other|Other
Queue|Queue
Name|Name
Size|Size
Progress|Progress
Speed|Speed
Eta|ETA
Status|Status
Source|Source
Details|Details
NoSelection|Select a download to see details
Ready|Ready
Idle|Idle
ActiveDownloads|Active
Queued|Queued
Probing|Probing
Downloading|Downloading
Merging|Merging
Paused|Paused
Failed|Failed
Canceled|Canceled
WaitingForSchedule|Scheduled
Completed|Completed
ClipboardDetected|Link found in clipboard
AddToQueue|Add to queue
Dismiss|Dismiss
DownloadRoot|Download folder
Browse|Browse…
Connections|Connections
SpeedLimit|Speed limit (KB/s, 0 = unlimited)
MaxConcurrent|Max concurrent downloads
PerHostLimit|Per-host limit
Proxy|Proxy URL
UserAgent|User-Agent
CategoryFolders|Category folders
EmbedMetadata|Embed metadata
EmbedThumbnail|Embed thumbnail
WriteSubtitles|Download subtitles
ScheduleEnabled|Schedule downloads
ScheduleStart|Start
ScheduleStop|Stop
AutoResume|Auto-resume on launch
StopWhenQueueEmpty|Exit when queue is empty
ClipboardMonitor|Monitor clipboard
StartWithWindows|Start with Windows
MinimizeToTray|Minimize to tray
CloseToTray|Close to tray
CookiesFromBrowser|Use cookies from browser
YtDlpUpdate|yt-dlp auto-update
Theme|Theme
Language|Language
Tools|Tools
YtDlp|yt-dlp
Ffmpeg|ffmpeg
ReadyStatus|Ready
Missing|Missing
Test|Test
UpdateTools|Update tools
FetchTools|Download tools
HostRegistration|Native host registration
RegisterHost|Register host
TestPing|Test connection
Extension|Browser extension
OpenExtensionFolder|Open extension folder
FirstRunTitle|Welcome to Media Catcher
FirstRunIntro|Check the items below to finish setup.
Refresh|Refresh
Analyze|Analyze
Analyzing|Analyzing…
Add|Add
AddPlaylist|Add playlist
Format|Format
Preset|Preset
FileName|File name (optional)
Category|Category
Url|URL
UrlPlaceholder|Paste a media or file URL
PlaylistItems|Playlist items
DirectDownload|Direct download
AnalyzeHint|Works with YouTube and yt-dlp supported sites
Duplicate|Already in the queue
Added|Added to the queue
Error|Error
DownloadFailed|Download failed
ToolMissing|Required tool is missing
AppNotRunning|App is not running
HostConnected|Connected
HostNotInstalled|Host not installed
RestartForLanguage|Restart the app to apply the language change.
OpenApp|Open app
Exit|Exit
ShowWindow|Show window
NoDownloads|No downloads yet
AddFirst|Click “Add URL” to get started
Clear|Clear
Apply|Apply
ConfirmClearHistory|Clear all finished downloads?
SelectFolder|Select folder
InvalidUrl|Invalid URL
UnsupportedUrl|This URL is not supported
DrmUnsupported|This content is DRM protected
ToolStatus|Tool status
ExtensionsHint|Chrome requires manual installation via chrome://extensions (Developer mode → Load unpacked).
InstallHostHint|Click “Register host” or run scripts/install-native-host.ps1
StorageHint|Settings and queue live in %APPDATA%\MediaCatcher
DownloadsCount|items
Browser|Browser
Clipboard|Clipboard
Manual|Manual
Takeover|Takeover
Priority|Priority
OpenSettings|Settings
Unknown|Unknown
'@

function ConvertTo-Pairs {
    param([string]$Text)
    $pairs = [ordered]@{}
    foreach ($rawLine in ($Text -split "`n")) {
        $line = $rawLine.TrimEnd("`r")
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separator = $line.IndexOf('|')
        if ($separator -lt 1) { throw "Invalid resource line: $line" }
        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1)
        if ($pairs.Contains($key)) { throw "Duplicate resource key: $key" }
        $pairs[$key] = $value
    }
    return $pairs
}

function Write-Resx {
    param([string]$Path, [System.Collections.Specialized.OrderedDictionary]$Pairs)
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<root>')
    $headers = @(
        @('resmimetype', 'text/microsoft-resx'),
        @('version', '2.0'),
        @('reader', 'System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),
        @('writer', 'System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
    )
    foreach ($header in $headers) {
        [void]$builder.AppendLine('  <resheader name="' + $header[0] + '">')
        [void]$builder.AppendLine('    <value>' + [System.Security.SecurityElement]::Escape($header[1]) + '</value>')
        [void]$builder.AppendLine('  </resheader>')
    }
    foreach ($key in $Pairs.Keys) {
        $escaped = [System.Security.SecurityElement]::Escape([string]$Pairs[$key])
        [void]$builder.AppendLine('  <data name="' + $key + '" xml:space="preserve">')
        [void]$builder.AppendLine('    <value>' + $escaped + '</value>')
        [void]$builder.AppendLine('  </data>')
    }
    [void]$builder.AppendLine('</root>')
    [System.IO.File]::WriteAllText($Path, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
}

$thai = ConvertTo-Pairs $thaiText
$english = ConvertTo-Pairs $englishText
$missing = $english.Keys | Where-Object { -not $thai.Contains($_) }
if ($missing) { throw "English keys missing from Thai: $($missing -join ', ')" }
$missing = $thai.Keys | Where-Object { -not $english.Contains($_) }
if ($missing) { throw "Thai keys missing from English: $($missing -join ', ')" }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Write-Resx -Path (Join-Path $OutputDirectory 'Strings.resx') -Pairs $thai
Write-Resx -Path (Join-Path $OutputDirectory 'Strings.en.resx') -Pairs $english
Write-Output "Wrote $($thai.Count) resource keys."
