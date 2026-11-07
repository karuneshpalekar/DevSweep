devsweep {VERSION} - command line for DevSweep for Windows
=========================================================

devsweep.exe is a single file; put it anywhere (for example in a folder on your PATH).

If Windows blocked the download: right-click the .zip > Properties > "Unblock" before
extracting, or run  Unblock-File .\devsweep.exe  in PowerShell.

Try:
    devsweep.exe scan                  overview sizes, changes nothing
    devsweep.exe scan --exact --explain
    devsweep.exe history
    devsweep.exe --help

No administrator rights are needed. The CLI and the DevSweep app share History, settings and the
quarantine in %LOCALAPPDATA%\DevSweep.
