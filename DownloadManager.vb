Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net
Imports System.Threading

Public Enum DownloadStatus
    Pending
    Downloading
    Completed
    Failed
    Paused
End Enum

''' <summary>
''' Một mục cần tải: dữ liệu URL/đường dẫn tương đối (FileDownloadData),
''' đường dẫn cục bộ đích, và trạng thái hiện tại.
''' </summary>
Public Class DownloadItem
    Public Property Data As FileDownloadData
    Public Property LocalPath As String
    ' DownloadStatus.Pending = 0 la gia tri mac dinh cua enum nen khong can gan tuong minh
    ' (tranh dung cu phap khoi tao gia tri cho auto-property de tuong thich vbc doi cu hon).
    Public Property Status As DownloadStatus
End Class

''' <summary>
''' Tải tệp tuần tự (từng tệp một) theo hàng đợi, có hỗ trợ TẠM DỪNG / TIẾP TỤC thật sự:
'''  - Dùng HttpWebRequest với header Range thay vì WebClient, nên khi tiếp tục,
'''    chương trình chỉ tải phần còn thiếu của tệp đang dở, không tải lại từ đầu.
'''  - Trạng thái hàng đợi (tệp nào xong, tệp nào còn) được lưu ra đĩa (DownloadQueueState),
'''    nên NGAY CẢ KHI TẮT CHƯƠNG TRÌNH giữa chừng, lần sau mở lại vẫn tiếp tục được.
''' Chạy trên 1 Thread nền, tự đánh dấu Invoke ở phía Form1 khi nhận sự kiện.
''' </summary>
Public Class DownloadManager
    Implements IDisposable

    Public Event FileStarted(item As DownloadItem)
    Public Event FileProgressChanged(item As DownloadItem, downloadedBytes As Long, totalBytes As Long)
    Public Event FileCompleted(item As DownloadItem, ex As Exception)
    Public Event QueuePaused(remainingCount As Integer)
    Public Event AllCompleted(totalOk As Integer, totalFail As Integer, wasCancelled As Boolean)

    Private Const BUFFER_SIZE As Integer = 65536

    Private _items As List(Of DownloadItem)
    Private _currentIndex As Integer
    Private _worker As Thread
    Private _pauseRequested As Boolean
    Private _cancelRequested As Boolean
    Private _totalOk As Integer
    Private _totalFail As Integer
    Private _statePath As String

    Public ReadOnly Property IsBusy As Boolean
        Get
            Return _worker IsNot Nothing AndAlso _worker.IsAlive
        End Get
    End Property

    ''' <summary>
    ''' Bắt đầu một phiên tải mới từ danh sách items.
    ''' statePath (tuỳ chọn): nơi lưu tiến độ hàng đợi để có thể tiếp tục sau khi đóng ứng dụng.
    ''' </summary>
    Public Sub Start(items As List(Of DownloadItem), Optional statePath As String = Nothing)
        _items = items
        _currentIndex = 0
        _totalOk = 0
        _totalFail = 0
        _cancelRequested = False
        _pauseRequested = False
        _statePath = statePath
        RunWorker()
    End Sub

    ''' <summary>Tiếp tục một phiên đã có sẵn (đã gọi Start trước đó rồi Pause, hoặc Load lại từ tệp trạng thái).</summary>
    Public Sub ContinueQueue(items As List(Of DownloadItem), Optional statePath As String = Nothing)
        _items = items
        _currentIndex = 0
        _totalOk = 0
        _totalFail = 0
        _cancelRequested = False
        _pauseRequested = False
        _statePath = statePath
        RunWorker()
    End Sub

    ''' <summary>Yêu cầu tạm dừng: tệp đang tải dở sẽ dừng đúng tại điểm đang tải (không mất phần đã tải).</summary>
    Public Sub Pause()
        _pauseRequested = True
    End Sub

    ''' <summary>Huỷ hẳn phiên tải, không lưu trạng thái để tiếp tục.</summary>
    Public Sub CancelAll()
        _cancelRequested = True
        _pauseRequested = True
    End Sub

    Private Sub RunWorker()
        _worker = New Thread(AddressOf WorkerLoop)
        _worker.IsBackground = True
        _worker.Start()
    End Sub

    Private Sub WorkerLoop()
        While _currentIndex < _items.Count
            If _cancelRequested Then Exit While

            If _pauseRequested Then
                SaveStateIfNeeded()
                RaiseEvent QueuePaused(_items.Count - _currentIndex)
                Return
            End If

            Dim item As DownloadItem = _items(_currentIndex)

            If item.Status = DownloadStatus.Completed Then
                _currentIndex += 1
                Continue While
            End If

            DownloadOne(item)

            If _cancelRequested Then Exit While
            If _pauseRequested Then
                SaveStateIfNeeded()
                RaiseEvent QueuePaused(_items.Count - _currentIndex)
                Return
            End If

            _currentIndex += 1
        End While

        If _cancelRequested Then
            DeleteStateIfNeeded()
            RaiseEvent AllCompleted(_totalOk, _totalFail, True)
        Else
            DeleteStateIfNeeded()
            RaiseEvent AllCompleted(_totalOk, _totalFail, False)
        End If
    End Sub

    Private Sub DownloadOne(item As DownloadItem)
        item.Status = DownloadStatus.Downloading
        RaiseEvent FileStarted(item)

        Try
            Dim dir As String = Path.GetDirectoryName(item.LocalPath)
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If
        Catch ex As Exception
            item.Status = DownloadStatus.Failed
            _totalFail += 1
            RaiseEvent FileCompleted(item, ex)
            Return
        End Try

        ' Số byte đã có sẵn trên đĩa (từ lần tải dở trước) - dùng để yêu cầu server
        ' chỉ gửi tiếp phần còn thiếu (Range request), thay vì tải lại từ đầu.
        Dim existingBytes As Long = 0
        If File.Exists(item.LocalPath) Then
            existingBytes = New FileInfo(item.LocalPath).Length
        End If

        Try
            Dim req As HttpWebRequest = CType(WebRequest.Create(item.Data.Url), HttpWebRequest)
            req.Method = "GET"
            req.Timeout = 30000
            req.ReadWriteTimeout = 30000
            req.UserAgent = "FileListDownloader/2CongLC"

            Dim requestedResume As Boolean = False
            If existingBytes > 0 AndAlso existingBytes <= Integer.MaxValue Then
                req.AddRange(CInt(existingBytes))
                requestedResume = True
            End If

            Using resp As HttpWebResponse = CType(req.GetResponse(), HttpWebResponse)

                Dim serverAcceptedRange As Boolean = requestedResume AndAlso resp.StatusCode = HttpStatusCode.PartialContent
                Dim startOffset As Long = If(serverAcceptedRange, existingBytes, 0)
                Dim totalBytes As Long = resp.ContentLength
                If serverAcceptedRange AndAlso totalBytes >= 0 Then totalBytes += existingBytes

                Dim mode As FileMode = If(serverAcceptedRange, FileMode.Append, FileMode.Create)

                Using fs As New FileStream(item.LocalPath, mode, FileAccess.Write)
                    Using respStream As Stream = resp.GetResponseStream()
                        Dim buffer(BUFFER_SIZE - 1) As Byte
                        Dim totalRead As Long = startOffset
                        Dim read As Integer = respStream.Read(buffer, 0, buffer.Length)

                        While read > 0
                            fs.Write(buffer, 0, read)
                            totalRead += read
                            RaiseEvent FileProgressChanged(item, totalRead, totalBytes)

                            If _cancelRequested OrElse _pauseRequested Then Exit While

                            read = respStream.Read(buffer, 0, buffer.Length)
                        End While
                    End Using
                End Using
            End Using

            If _cancelRequested Then
                item.Status = DownloadStatus.Failed
                _totalFail += 1
                RaiseEvent FileCompleted(item, Nothing)
            ElseIf _pauseRequested Then
                item.Status = DownloadStatus.Paused
                ' Không raise FileCompleted khi tạm dừng - tệp sẽ tiếp tục ở lần Resume kế tiếp.
            Else
                item.Status = DownloadStatus.Completed
                _totalOk += 1
                RaiseEvent FileCompleted(item, Nothing)
            End If

        Catch ex As WebException When TryHandleRangeNotSatisfiable(ex, item)
            ' Server báo 416 (Range không hợp lệ) - nghĩa là tệp trên đĩa đã đủ/đúng kích thước rồi.
            item.Status = DownloadStatus.Completed
            _totalOk += 1
            RaiseEvent FileCompleted(item, Nothing)

        Catch ex As Exception
            If _pauseRequested Then
                item.Status = DownloadStatus.Paused
            Else
                item.Status = DownloadStatus.Failed
                _totalFail += 1
                RaiseEvent FileCompleted(item, ex)
            End If
        End Try
    End Sub

    Private Function TryHandleRangeNotSatisfiable(ex As WebException, item As DownloadItem) As Boolean
        Dim resp As HttpWebResponse = TryCast(ex.Response, HttpWebResponse)
        Return resp IsNot Nothing AndAlso resp.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable
    End Function

    Private Sub SaveStateIfNeeded()
        If String.IsNullOrWhiteSpace(_statePath) Then Return
        Try
            DownloadQueueState.Save(_items, _statePath)
        Catch
        End Try
    End Sub

    Private Sub DeleteStateIfNeeded()
        If String.IsNullOrWhiteSpace(_statePath) Then Return
        DownloadQueueState.Delete(_statePath)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        _cancelRequested = True
    End Sub

End Class
