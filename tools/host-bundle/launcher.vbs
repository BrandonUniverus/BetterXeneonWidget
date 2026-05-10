' Launches BetterXeneonWidget.Host.exe with no visible console window.
' Used by the HKCU\Run autostart entry and by manual "Start service now" runs.
Set Shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
ScriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
Shell.CurrentDirectory = ScriptDir
Shell.Run """" & ScriptDir & "\BetterXeneonWidget.Host.exe""", 0, False
