' GPO user logon launcher — same pattern as the original
' Company_Code_Selection_&&&.vbs:
'   GPO runs this VBS, then PowerShell runs a LOCAL script on the RDS host.
'
' Preferred PS1 (copy once per RDS host, not per company code):
'   C:\GSS\PowerShell\CompanyCodes\Apply-CompanyCodes.ps1
' Fallback: Apply-CompanyCodes.ps1 sitting next to this VBS (SYSVOL Show Files).
'
' Always waits (third argument True) so GSSLOGIN.DIR exists before Global Shop starts.
Option Explicit

Dim fso, sh, folder, localPs1, siblingPs1, ps1, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("Wscript.Shell")

folder     = fso.GetParentFolderName(WScript.ScriptFullName)
localPs1   = "C:\GSS\PowerShell\CompanyCodes\Apply-CompanyCodes.ps1"
siblingPs1 = folder & "\Apply-CompanyCodes.ps1"

WriteLog "VBS started. Script=" & WScript.ScriptFullName

If fso.FileExists(localPs1) Then
    ps1 = localPs1
ElseIf fso.FileExists(siblingPs1) Then
    ps1 = siblingPs1
Else
    WriteLog "ERROR: Apply-CompanyCodes.ps1 not found at " & localPs1 & " or " & siblingPs1
    WScript.Quit 0
End If

WriteLog "Launching " & ps1
cmd = "powershell.exe -NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & ps1 & """"
sh.Run cmd, 0, True
WriteLog "PowerShell finished."

Sub WriteLog(msg)
    On Error Resume Next
    Dim logPath, logFile
    logPath = sh.ExpandEnvironmentStrings("%TEMP%") & "\Apply-CompanyCodes.log"
    Set logFile = fso.OpenTextFile(logPath, 8, True)
    logFile.WriteLine Now & " " & msg
    logFile.Close
    On Error GoTo 0
End Sub
