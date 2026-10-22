# Master Plan: Next-Gen Download Manager (IDM Clone)

## 1. Project Overview
The goal is to build a high-performance download manager that accelerates downloads by splitting files into dynamic segments, features a modern UI, integrates seamlessly with browsers to capture download links (and media), and is packaged as a standalone `.exe` installer.

### Core Features
*   **Dynamic File Segmentation:** Splitting files into multiple chunks (HTTP `Range` requests) for parallel downloading.
*   **Pause & Resume Capability:** Saving download states to resume interrupted downloads.
*   **Browser Integration:** Catching downloads from Chrome, Edge, and Firefox.
*   **Video Grabber:** Detecting video/audio streams on web pages.
*   **Queue Management:** Scheduling and categorizing downloads.
*   **Speed Limiter:** Throttling network usage.

---

## 2. Technology Stack
To write clean, human-readable, and highly performant code, we will use the following stack:
*   **Core Application & UI:** C# with .NET 8 (WPF - Windows Presentation Foundation). C# is incredibly readable, has excellent networking libraries, and builds beautiful native Windows apps.
*   **Browser Extension:** WebExtensions API (JavaScript, HTML, CSS).
*   **App-Browser Communication:** Native Messaging Host (JSON via Standard Input/Output).
*   **Installer (The .exe maker):** Inno Setup (A free, script-driven compiler to generate professional Windows installers).

---

## 3. Comprehensive File Structure
When you set up your project locally, your directory should look exactly like this:

```text
DownloadManagerProject/
│
├── AppCore/                           # The Desktop Application (C# .NET WPF)
│   ├── DownloadManager.sln            # Visual Studio Solution File
│   ├── UI/                            
│   │   ├── MainWindow.xaml            # Main user interface
│   │   └── MainWindow.xaml.cs         # UI Logic
│   ├── Engine/                        
│   │   ├── DownloadTask.cs            # Manages a single file download
│   │   ├── SegmentWorker.cs           # Manages a single chunk/thread
│   │   └── SpeedLimiter.cs            # Network throttling logic
│   ├── Utils/                         
│   │   └── NativeMessagingHost.cs     # Listens to the browser extension
│   └── Models/                        
│       └── DownloadItem.cs            # Data structure for downloads
│
├── BrowserExtension/                  # The Chrome/Edge Extension
│   ├── manifest.json                  # Extension configuration
│   ├── background.js                  # Intercepts downloads & talks to C# app
│   ├── content.js                     # Sniffs for videos on web pages
│   ├── popup.html                     # Extension UI
│   └── icons/                         
│       └── icon128.png                
│
└── Installer/                         # Executable Generator
    └── setup_script.iss               # Inno Setup configuration
```

---

## 4. Implementation Phases & Human-Like Code Examples

### Phase 1: The Core Download Engine (C#)
The secret to IDM's speed is making multiple connections for a single file. We do this using HTTP Range headers.

**File:** `AppCore/Engine/SegmentWorker.cs`
```csharp
using System.Net.Http;
using System.IO;
using System.Threading.Tasks;

public class SegmentWorker
{
    private readonly string _url;
    private readonly long _startByte;
    private readonly long _endByte;
    private readonly string _tempFilePath;

    // Constructor setup is clean and clearly named
    public SegmentWorker(string url, long startByte, long endByte, string tempFilePath)
    {
        _url = url;
        _startByte = startByte;
        _endByte = endByte;
        _tempFilePath = tempFilePath;
    }

    // This method downloads a specific chunk of the file
    public async Task DownloadChunkAsync()
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, _url);
        
        // This is the magic line that tells the server we only want a specific part of the file
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(_startByte, _endByte);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        // Copy the downloaded bytes to our temporary file on disk
        await stream.CopyToAsync(fileStream);
    }
}
```

### Phase 2: The Browser Extension (JavaScript)
We need to capture downloads before the browser handles them and send them to our C# app.

**File:** `BrowserExtension/manifest.json`
```json
{
  "manifest_version": 3,
  "name": "Super Download Integrator",
  "version": "1.0",
  "permissions": [
    "downloads",
    "nativeMessaging",
    "webRequest"
  ],
  "background": {
    "service_worker": "background.js"
  }
}
```

**File:** `BrowserExtension/background.js`
```javascript
// Listen for any download initiated in the browser
chrome.downloads.onCreated.addListener((downloadItem) => {
    
    // Pause the browser's default download immediately
    chrome.downloads.pause(downloadItem.id);
    
    console.log("Caught download URL:", downloadItem.url);

    // Send the URL to our C# Desktop Application via Native Messaging
    const port = chrome.runtime.connectNative('com.mycompany.downloadmanager');
    
    port.postMessage({ 
        action: "START_DOWNLOAD", 
        url: downloadItem.url,
        filename: downloadItem.filename
    });

    // Cancel the browser's download since our app is taking over
    chrome.downloads.cancel(downloadItem.id);
});
```

### Phase 3: Compiling to a .EXE Installer
To create the `.exe` file that users will install, you will compile your C# project into an executable, and then use **Inno Setup** to package it. 

**File:** `Installer/setup_script.iss`
```pascal
; This script generates the final setup.exe for the user

[Setup]
AppName=NextGen Download Manager
AppVersion=1.0
DefaultDirName={pf}\NextGen Download Manager
DefaultGroupName=NextGen Download Manager
OutputDir=.\Output
OutputBaseFilename=DownloadManager_Installer
Compression=lzma
SolidCompression=yes

[Files]
; Grab the compiled C# application files
Source: "..\AppCore\bin\Release\net8.0-windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Include the browser extension folder so it can be sideloaded
Source: "..\BrowserExtension\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Create desktop and start menu shortcuts
Name: "{group}\NextGen Download Manager"; Filename: "{app}\DownloadManager.exe"
Name: "{commondesktop}\NextGen Download Manager"; Filename: "{app}\DownloadManager.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"

[Run]
; Launch the app automatically after the user finishes installing
Filename: "{app}\DownloadManager.exe"; Description: "Launch NextGen Download Manager"; Flags: nowait postinstall skipifsilent
```

---

## 5. Next Steps for Development
1. **Install Tools:** Download Visual Studio 2022 (for C#), VS Code (for the extension), and Inno Setup (for the `.exe`).
2. **Build the Engine:** Start by creating the `SegmentWorker` to successfully download a file in chunks from a direct URL.
3. **Build the UI:** Hook the engine up to a WPF window so you have a visual progress bar.
4. **Link the Browser:** Register the Native Messaging Host in your Windows Registry so `background.js` can successfully talk to your C# app.
5. **Compile & Package:** Build the Release version in Visual Studio, run the `setup_script.iss`, and you will have your final `DownloadManager_Installer.exe`.