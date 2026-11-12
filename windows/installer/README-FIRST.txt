DevSweep for Windows {VERSION} - portable
=========================================

1. Unblock the download (once)
   DevSweep is not code-signed, so Windows marks the downloaded zip as coming
   from the internet and SmartScreen may stop it. Before you extract it, either
   right-click the .zip > Properties > tick "Unblock" > OK, or in PowerShell:

       Unblock-File "$HOME\Downloads\DevSweep-{VERSION}-win-x64-portable.zip"

   (If you already extracted it, run this inside the extracted folder:
       Get-ChildItem -Recurse | Unblock-File )

2. Run DevSweep.exe
   If Windows still says "Windows protected your PC", click "More info" and
   then "Run anyway".

Notes
-----
- Windows 11 (22H2 or later), 64-bit. No administrator rights are needed and
  nothing is installed: delete this folder to remove the program.
- Keep the folder together; DevSweep.exe needs the files next to it.
- History, settings and the quarantine are stored in %LOCALAPPDATA%\DevSweep,
  not in this folder, so they survive replacing or deleting the folder.
- Prefer a Start Menu entry and easy upgrades? Use the
  DevSweep-{VERSION}-win-x64-setup.exe installer instead.
- Check your download against SHA256SUMS.txt from the release page.
- Want to check DevSweep's numbers against Windows on your own PC? The scripts folder holds
  Verify-DevSweep.ps1 (read-only by default, nothing is uploaded). Open PowerShell in this folder and run:
      powershell -ExecutionPolicy Bypass -File .\scripts\Verify-DevSweep.ps1
