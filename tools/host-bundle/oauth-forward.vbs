' URI-scheme handler for `betterxeneonwidget://callback?code=...&state=...`.
' Registered in HKCU\Software\Classes\betterxeneonwidget by the installer.
' Spotify redirects the browser here; Windows hands us the URL as arg 0; we
' forward the query string to the running host's HTTP callback endpoint.
'
' Run via wscript.exe (no console window). Failures are silent — the widget
' will simply stay in "Connecting..." until the user retries. Better than
' a console flash for a smooth UX.

On Error Resume Next

If WScript.Arguments.Count = 0 Then WScript.Quit 0

Dim url, qPos, query, http
url = WScript.Arguments(0)
qPos = InStr(url, "?")
If qPos > 0 Then
    query = Mid(url, qPos)
Else
    query = ""
End If

Set http = CreateObject("MSXML2.XMLHTTP")
http.Open "GET", "http://127.0.0.1:8976/api/spotify/callback" & query, False
http.Send
