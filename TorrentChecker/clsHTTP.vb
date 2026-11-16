'HTTPClient known bug:
'timeout does not affect DNS resolution

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Web

Public Class clsHTTP
    Class HTTPResult
        Public Property Result As String
        Public Property ResultB As Byte()
        Public Property Success As Boolean
        Public Property Headers As Headers.HttpContentHeaders
    End Class

    Private Const http_timeout_sec As Double = 30
    Shared cookie_container As New CookieContainer
    Private http_handler As New HttpClientHandler With {.CookieContainer = cookie_container, .UseProxy = False}
    Private http_request As New HttpClient(http_handler) With {.Timeout = TimeSpan.FromSeconds(http_timeout_sec)}
    Private cts As New CancellationTokenSource()
    Private proxyEndpoint As Uri
    Private proxyAuth As NetworkCredential

    Public Function Cancel() As CancellationToken
        'Cancel all requests
        cts.Cancel()
        cts.Dispose()
        cts = New CancellationTokenSource()
        Return cts.Token
    End Function

    Public Sub ClearCookies(url As Uri)
        For Each c As Cookie In cookie_container.GetCookies(url)
            c.Expired = True
        Next
    End Sub

    Friend Sub ConfigureProxy(settings As Dictionary(Of String, Object), credentials As NetworkCredential)
        Dim endpoint As Uri = Nothing
        Dim auth As NetworkCredential = Nothing
        If CBool(settings("use_proxy")) Then
            endpoint = clsSettingsStore.ProxyEndpoint(settings)
            If endpoint Is Nothing Then Throw New ArgumentException("Некорректный адрес или порт прокси.")
            If credentials.UserName.Length <> 0 OrElse credentials.Password.Length <> 0 Then auth = credentials
        End If
        If Object.Equals(proxyEndpoint, endpoint) AndAlso Object.ReferenceEquals(proxyAuth, auth) Then Return

        Cancel()
        http_request.Dispose()
        http_handler = New HttpClientHandler With {.CookieContainer = cookie_container, .UseProxy = endpoint IsNot Nothing}
        If endpoint IsNot Nothing Then
            http_handler.Proxy = New WebProxy(endpoint) With {.Credentials = auth}
            http_handler.PreAuthenticate = True
        End If
        http_request = New HttpClient(http_handler) With {.Timeout = TimeSpan.FromSeconds(http_timeout_sec)}
        proxyEndpoint = endpoint
        proxyAuth = auth
    End Sub

    Public Async Function HTTPGet(ByVal Url As String, Optional ByVal GetData As Dictionary(Of String, String) = Nothing, Optional ByVal asBinary As Boolean = False, Optional ByVal requireSuccessStatus As Boolean = False) As Task(Of HTTPResult)
        Dim ret As New HTTPResult
        Dim token As CancellationToken = cts.Token

        Try
            If GetData IsNot Nothing Then
                Url &= "?"
                Url &= String.Join("&", GetData.Select(Function(kvp) String.Format("{0}={1}", kvp.Key, Uri.EscapeDataString(kvp.Value))))
            End If
            Using request As New HttpRequestMessage(HttpMethod.Get, Url)
                request.Headers.ConnectionClose = True
                request.Headers.Add("User-Agent", APP_NAME)
                Using Response As HttpResponseMessage = Await http_request.SendAsync(request, token)
                    If requireSuccessStatus Then Response.EnsureSuccessStatusCode()
                    If asBinary Then
                        ret.ResultB = Await Response.Content.ReadAsByteArrayAsync
                    Else
                        If Response.Content.Headers.ContentType IsNot Nothing AndAlso
                            LCase(Response.Content.Headers.ContentType.CharSet) = "cp1251" Then
                            Response.Content.Headers.ContentType.CharSet = "windows-1251"
                        End If
                        ret.Result = Await Response.Content.ReadAsStringAsync
                    End If
                    ret.Headers = Response.Content.Headers
                    ret.Success = True
                End Using
                Return ret
            End Using
        Catch ex As TaskCanceledException
            If Not token.IsCancellationRequested Then
                'timed out
                Throw New TimeoutException("The operation has timed out.", New Exception(Url))
            End If

            ret.Success = False
            Return ret
        End Try
    End Function

    Public Async Function HTTPPost(ByVal Url As String, ByVal PostData As Dictionary(Of String, String), Optional ByVal asBinary As Boolean = False) As Task(Of HTTPResult)
        Dim ret As New HTTPResult
        Dim token As CancellationToken = cts.Token
        Dim post_encoding As String = "windows-1251"
        Dim postData_encoded As String = String.Join("&", PostData.Select(Function(kvp) String.Format("{0}={1}", kvp.Key, HttpUtility.UrlEncode(kvp.Value, Encoding.GetEncoding(post_encoding)))))
        Dim postData_bytes As Byte() = Encoding.GetEncoding(post_encoding).GetBytes(postData_encoded)

        Try
            Using content As ByteArrayContent = New ByteArrayContent(postData_bytes)
                content.Headers.Add("Content-Type", "application/x-www-form-urlencoded")
                Using request As New HttpRequestMessage(HttpMethod.Post, Url)
                    request.Content = content
                    request.Headers.ConnectionClose = True
                    request.Headers.ExpectContinue = False
                    request.Headers.Accept.Add(New Headers.MediaTypeWithQualityHeaderValue("application/x-www-form-urlencoded"))
                    request.Headers.Add("User-Agent", APP_NAME)
                    Using Response As HttpResponseMessage = Await http_request.SendAsync(request, token)
                        With Response
                            If asBinary Then
                                ret.ResultB = Await Response.Content.ReadAsByteArrayAsync
                            Else
                                If Response.Content.Headers.ContentType IsNot Nothing AndAlso
                                    LCase(Response.Content.Headers.ContentType.CharSet) = "cp1251" Then
                                    Response.Content.Headers.ContentType.CharSet = "windows-1251"
                                End If
                                ret.Result = Await Response.Content.ReadAsStringAsync()
                            End If
                            ret.Headers = Response.Content.Headers
                            ret.Success = True
                        End With
                    End Using
                    Return ret
                End Using
            End Using
        Catch ex As TaskCanceledException
            If Not token.IsCancellationRequested Then
                'timed out
                Throw New TimeoutException("The operation has timed out.", New Exception(Url))
            End If

            ret.Success = False
            Return ret
        End Try
    End Function

    Public Sub New()
        ServicePointManager.DefaultConnectionLimit = 5
    End Sub
End Class
