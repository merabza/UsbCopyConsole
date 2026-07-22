# UsbCopyConsole

Interactive menu console client for UsbCopyService. Starts a copy job on the service side, receives
optimized packages (zip archives of small files, whole medium files, byte-range parts of large files),
verifies SHA256 of every package, restores the original structure into a local folder (USB drive) and
acknowledges each package so the service can free its temporary files.

Re-running a project is incremental: files already present in the destination folder are reported to the
service and are not transferred again.

## Project references

This solution references existing clones:

- `D:\1WorkDotnet\UsbCopy\{AppCliTools, ParametersManagement, SystemTools}`
- Shared contracts: `D:\1WorkDotnet\usbCopyService\UsbCopyServiceShared\UsbCopyServiceShared.Contracts`

## Build and run

```powershell
dotnet build UsbCopyConsole.slnx

dotnet run --project UsbCopyConsole -- --use "<path>\UsbCopyConsole.json"
```

The parameters file is edited through the application menus. Structure:

```json
{
  "LogFolder": "",
  "ApiClients": {
    "UsbCopyServiceMain": {
      "Server": "http://192.168.1.50:5099",
      "ApiKey": "SameKeyAsInServiceAppsettings"
    }
  },
  "Projects": {
    "MyProject": {
      "LocalPath": "E:\\UsbBackups",
      "ApiClientName": "UsbCopyServiceMain",
      "RemoteProjectName": "MyProject"
    }
  }
}
```

Menu flow: main menu → project → `ReceiveFiles`. The destination subfolder is named `yyyyMMddHHmmss`
inside `LocalPath` (reusing the latest existing one is offered for confirmation, like in UsbCopy).

Note: the service authorizes by api key AND caller IP (`ApiKeys` section on the service side must contain
this client's IP as the service sees it).
