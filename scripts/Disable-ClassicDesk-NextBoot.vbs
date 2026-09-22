Option Explicit
Dim shell, fso, executable, dotnet
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
executable = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "ClassicDesk.exe")
dotnet = shell.ExpandEnvironmentStrings("%USERPROFILE%\.dotnet")
If fso.FileExists(fso.BuildPath(dotnet, "dotnet.exe")) Then
  shell.Environment("PROCESS")("DOTNET_ROOT") = dotnet
  shell.Environment("PROCESS")("DOTNET_ROOT_X64") = dotnet
End If
shell.Run Chr(34) & executable & Chr(34) & " --disable-shell-service", 0, False
