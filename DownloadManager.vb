Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Net

Public Enum DownloadStatus
    Pending
    Downloading
    Completed
    Failed
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
''' Tải tệp tuần tự (từng tệp một) theo hàng đợi, thay vì mở hàng loạt WebClient
''' cùng lúc như bản cũ (dễ nghẽn băng thông / socket khi danh sách dài).
''' Bắn sự kiện cho UI cập nhật tiến độ tệp hiện tại, tổng số đã xong/lỗi,
''' và khi toàn bộ hàng đợi hoàn tất.
''' </summary>
Public Class DownloadManager
    Implements IDisposable

    Public Event FileStarted(item As DownloadItem)
    Public Event FileProgressChanged(item As DownloadItem, e As DownloadProgressChangedEventArgs)
    Public Event FileCompleted(item As DownloadItem, ex As Exception)
    Public Event AllCompleted(totalOk As Integer, totalFail As Integer)

    Private ReadOnly _queue As New Queue(Of DownloadItem)
    Private _client As WebClient
    Private _current As DownloadItem
    Private _totalOk As Integer
    Private _totalFail As Integer
    Private _cancelled As Boolean

    ''' <summary>Bắt đầu tải toàn bộ danh sách items, tuần tự từng tệp.</summary>
    Public Sub Start(items As List(Of DownloadItem))
        _queue.Clear()
        For Each it As DownloadItem In items
            _queue.Enqueue(it)
        Next
        _totalOk = 0
        _totalFail = 0
        _cancelled = False
        DownloadNext()
    End Sub

    ''' <summary>Huỷ tệp đang tải và dừng hàng đợi.</summary>
    Public Sub CancelAll()
        _cancelled = True
        Try
            If _client IsNot Nothing AndAlso _client.IsBusy Then
                _client.CancelAsync()
            End If
        Catch
        End Try
    End Sub

    Private Sub DownloadNext()
        If _cancelled Then
            RaiseEvent AllCompleted(_totalOk, _totalFail)
            Return
        End If

        If _queue.Count = 0 Then
            RaiseEvent AllCompleted(_totalOk, _totalFail)
            Return
        End If

        _current = _queue.Dequeue()
        _current.Status = DownloadStatus.Downloading
        RaiseEvent FileStarted(_current)

        Try
            Dim dir As String = Path.GetDirectoryName(_current.LocalPath)
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If
        Catch ex As Exception
            _current.Status = DownloadStatus.Failed
            _totalFail += 1
            RaiseEvent FileCompleted(_current, ex)
            DownloadNext()
            Return
        End Try

        _client = New WebClient()
        AddHandler _client.DownloadProgressChanged, AddressOf OnProgress
        AddHandler _client.DownloadFileCompleted, AddressOf OnCompleted

        Try
            _client.DownloadFileAsync(New Uri(_current.Data.Url), _current.LocalPath)
        Catch ex As Exception
            CleanupClient()
            _current.Status = DownloadStatus.Failed
            _totalFail += 1
            RaiseEvent FileCompleted(_current, ex)
            DownloadNext()
        End Try
    End Sub

    Private Sub OnProgress(sender As Object, e As DownloadProgressChangedEventArgs)
        RaiseEvent FileProgressChanged(_current, e)
    End Sub

    Private Sub OnCompleted(sender As Object, e As AsyncCompletedEventArgs)
        CleanupClient()

        Dim ok As Boolean = (e.Error Is Nothing) AndAlso Not e.Cancelled
        If ok Then
            _current.Status = DownloadStatus.Completed
            _totalOk += 1
        Else
            _current.Status = DownloadStatus.Failed
            _totalFail += 1
        End If
        RaiseEvent FileCompleted(_current, e.Error)

        DownloadNext()
    End Sub

    Private Sub CleanupClient()
        Try
            If _client IsNot Nothing Then
                RemoveHandler _client.DownloadProgressChanged, AddressOf OnProgress
                RemoveHandler _client.DownloadFileCompleted, AddressOf OnCompleted
                _client.Dispose()
            End If
        Catch
        Finally
            _client = Nothing
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        CleanupClient()
    End Sub

End Class
