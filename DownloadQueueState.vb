Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text

''' <summary>
''' Lưu và khôi phục danh sách DownloadItem (kèm trạng thái) ra một tệp văn bản đơn giản,
''' để phiên tải dở có thể được TIẾP TỤC ngay cả sau khi người dùng đóng chương trình.
''' Định dạng mỗi dòng: TrangThai|DuongDanCucBo|Url
''' (dùng dấu | làm phân cách; phần Url được nối lại nếu bản thân nó chứa dấu |).
''' </summary>
Public Class DownloadQueueState

    Private Const SEP As Char = "|"c

    Public Shared Sub Save(items As List(Of DownloadItem), statePath As String)
        Dim dir As String = Path.GetDirectoryName(statePath)
        If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If

        Dim sb As New StringBuilder()
        For Each it As DownloadItem In items
            sb.Append(it.Status.ToString()).Append(SEP).Append(it.LocalPath).Append(SEP).Append(it.Data.Url).AppendLine()
        Next

        File.WriteAllText(statePath, sb.ToString(), Encoding.UTF8)
    End Sub

    ''' <summary>
    ''' Đọc lại danh sách đã lưu. Các mục Completed vẫn giữ nguyên trạng thái (để bỏ qua khi tải lại),
    ''' các mục khác (Pending/Downloading/Paused/Failed) đều được đưa về Pending để thử tải lại/tiếp tục.
    ''' </summary>
    Public Shared Function Load(statePath As String) As List(Of DownloadItem)
        Dim result As New List(Of DownloadItem)
        If Not File.Exists(statePath) Then Return result

        Dim lines As String() = File.ReadAllLines(statePath, Encoding.UTF8)
        For Each line As String In lines
            If String.IsNullOrWhiteSpace(line) Then Continue For

            Dim parts As String() = line.Split(SEP)
            If parts.Length < 3 Then Continue For

            Try
                Dim rawStatus As String = parts(0)
                Dim localPath As String = parts(1)
                Dim url As String = String.Join(SEP.ToString(), parts, 2, parts.Length - 2)

                Dim data As New FileDownloadData(url)
                Dim item As New DownloadItem
                item.Data = data
                item.LocalPath = localPath

                If rawStatus = DownloadStatus.Completed.ToString() Then
                    item.Status = DownloadStatus.Completed
                Else
                    item.Status = DownloadStatus.Pending
                End If

                result.Add(item)
            Catch
                ' Bỏ qua dòng lỗi định dạng, không làm hỏng toàn bộ danh sách
            End Try
        Next

        Return result
    End Function

    Public Shared Function Exists(statePath As String) As Boolean
        Return File.Exists(statePath)
    End Function

    Public Shared Sub Delete(statePath As String)
        Try
            If File.Exists(statePath) Then File.Delete(statePath)
        Catch
        End Try
    End Sub

End Class
