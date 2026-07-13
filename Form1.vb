Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Net
Imports System.Windows.Forms

''' <summary>
''' Công cụ tạo danh sách URL từ một thư mục tệp, và tải hàng loạt tệp
''' theo danh sách đó về máy, có tiến độ theo từng tệp và tổng thể,
''' hỗ trợ TẠM DỪNG / TIẾP TỤC (kể cả sau khi đóng và mở lại chương trình).
''' Viết lại theo hướng tách lớp: FileListBuilder lo việc quét/sinh danh sách,
''' DownloadManager lo việc tải tuần tự (dùng Range request để tiếp tục đúng chỗ),
''' DownloadQueueState lo việc lưu/khôi phục tiến độ ra đĩa,
''' Form1 chỉ còn nhiệm vụ hiển thị.
''' Giao diện dựng bằng code (không dùng Designer.vb) để build được bằng vbc.exe.
''' </summary>
Public Class Form1
    Inherits Form

    ' ==== Khối 1: Tạo danh sách URL ====
    ' Ghi chú: các control có gắn "Handles X.Click"/"Handles X.TextChanged" bắt buộc
    ' phải khai báo WithEvents, nếu không vbc sẽ báo lỗi "Handles clause requires a WithEvents variable".
    Private txtSourceFolder As TextBox
    Private WithEvents btnBrowseSource As Button
    Private txtPattern As TextBox
    Private txtBaseUrl As TextBox
    Private txtListName As TextBox
    Private WithEvents btnGenerateList As Button

    ' ==== Khối 2: Tải tệp theo danh sách ====
    Private cboFileList As ComboBox
    Private WithEvents btnRefreshList As Button
    Private WithEvents btnOpenLocation As Button
    Private WithEvents txtProjectName As TextBox
    Private txtDownloadRoot As TextBox
    Private WithEvents btnBrowseDownloadRoot As Button
    Private WithEvents btnStartDownload As Button
    Private WithEvents btnPauseDownload As Button
    Private WithEvents btnResumeDownload As Button
    Private WithEvents btnCancelDownload As Button

    Private progressCurrentFile As ProgressBar
    Private lblCurrentFileInfo As Label
    Private progressDone As ProgressBar
    Private lblDonePercent As Label
    Private progressFailed As ProgressBar
    Private lblFailedPercent As Label
    Private lblSummary As Label
    Private lblResumeHint As Label

    Private folderDialog As FolderBrowserDialog

    ' ==== Trạng thái tải ====
    Private _downloadManager As DownloadManager
    Private _totalItems As Integer

    Public Sub New()
        InitializeComponent()
    End Sub

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Try
            txtDownloadRoot.Text = Path.Combine(Directory.GetCurrentDirectory(), "Download")
            RefreshFileListCombo()
            UpdateResumeHint()
        Catch ex As Exception
        End Try
    End Sub

    ' ========================================================================
    '  KHỐI 1 - TẠO DANH SÁCH URL
    ' ========================================================================

    Private Sub btnBrowseSource_Click(sender As Object, e As EventArgs) Handles btnBrowseSource.Click
        If folderDialog.ShowDialog() = DialogResult.OK Then
            txtSourceFolder.Text = folderDialog.SelectedPath
        End If
    End Sub

    Private Sub btnGenerateList_Click(sender As Object, e As EventArgs) Handles btnGenerateList.Click
        If String.IsNullOrWhiteSpace(txtSourceFolder.Text) Then
            MessageBox.Show("Vui lòng chọn thư mục nguồn.")
            Return
        End If
        If Not Directory.Exists(txtSourceFolder.Text) Then
            MessageBox.Show("Thư mục nguồn không tồn tại.")
            Return
        End If

        Dim pattern As String = txtPattern.Text
        If String.IsNullOrWhiteSpace(pattern) Then pattern = "*.*"
        If Not pattern.StartsWith("*.") Then
            MessageBox.Show("Mẫu tệp phải bắt đầu bằng *. (vd: *.png, *.*)")
            Return
        End If

        Try
            Dim urls As List(Of String) = FileListBuilder.BuildUrlList(txtSourceFolder.Text, pattern, txtBaseUrl.Text)

            If urls.Count = 0 Then
                MessageBox.Show("Không tìm thấy tệp nào khớp với mẫu đã chọn.")
                Return
            End If

            Dim dataFolder As String = Path.Combine(Directory.GetCurrentDirectory(), "data")
            Dim savedPath As String = FileListBuilder.SaveList(urls, dataFolder, txtListName.Text)

            RefreshFileListCombo()
            cboFileList.SelectedItem = savedPath

            MessageBox.Show("Đã tạo danh sách gồm " & urls.Count & " tệp." & vbNewLine &
                             "Lưu tại: " & savedPath)
        Catch ex As Exception
            MessageBox.Show("Lỗi khi tạo danh sách: " & ex.Message)
        End Try
    End Sub

    ' ========================================================================
    '  KHỐI 2 - TẢI TỆP THEO DANH SÁCH (có Tạm dừng / Tiếp tục)
    ' ========================================================================

    Private Sub RefreshFileListCombo()
        Dim dataFolder As String = Path.Combine(Directory.GetCurrentDirectory(), "data")
        cboFileList.Items.Clear()
        If Directory.Exists(dataFolder) Then
            Dim files As String() = Directory.GetFiles(dataFolder, "*.txt", SearchOption.AllDirectories)
            cboFileList.Items.AddRange(files)
        End If
    End Sub

    Private Sub btnRefreshList_Click(sender As Object, e As EventArgs) Handles btnRefreshList.Click
        RefreshFileListCombo()
    End Sub

    Private Sub btnOpenLocation_Click(sender As Object, e As EventArgs) Handles btnOpenLocation.Click
        Try
            Dim path As String = cboFileList.Text
            If Not String.IsNullOrWhiteSpace(path) AndAlso File.Exists(path) Then
                Process.Start("explorer.exe", "/select," & path)
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Sub btnBrowseDownloadRoot_Click(sender As Object, e As EventArgs) Handles btnBrowseDownloadRoot.Click
        If folderDialog.ShowDialog() = DialogResult.OK Then
            txtDownloadRoot.Text = folderDialog.SelectedPath
        End If
    End Sub

    Private Sub txtProjectName_TextChanged(sender As Object, e As EventArgs) Handles txtProjectName.TextChanged
        UpdateResumeHint()
    End Sub

    ''' <summary>Tên dự án hiện tại trên form (mặc định "project1" nếu để trống).</summary>
    Private Function CurrentProjectName() As String
        Return If(String.IsNullOrWhiteSpace(txtProjectName.Text), "project1", txtProjectName.Text)
    End Function

    ''' <summary>Đường dẫn tệp trạng thái hàng đợi (dùng để tạm dừng/tiếp tục) cho một dự án.</summary>
    Private Function GetStatePath(projectName As String) As String
        Dim tempFolder As String = Path.Combine(Directory.GetCurrentDirectory(), "temp")
        Return Path.Combine(tempFolder, projectName & "-queue.txt")
    End Function

    ''' <summary>Cập nhật gợi ý + trạng thái nút Tiếp tục dựa theo tên dự án đang nhập.</summary>
    Private Sub UpdateResumeHint()
        If _downloadManager IsNot Nothing AndAlso _downloadManager.IsBusy Then Return

        Dim statePath As String = GetStatePath(CurrentProjectName())
        Dim hasSavedState As Boolean = DownloadQueueState.Exists(statePath)

        btnResumeDownload.Enabled = hasSavedState
        If hasSavedState Then
            lblResumeHint.Text = "Phát hiện phiên tải dở cho dự án """ & CurrentProjectName() & """ - bấm ""Tiếp tục"" để tải nốt."
        Else
            lblResumeHint.Text = ""
        End If
    End Sub

    Private Sub btnStartDownload_Click(sender As Object, e As EventArgs) Handles btnStartDownload.Click
        If String.IsNullOrWhiteSpace(cboFileList.Text) OrElse Not File.Exists(cboFileList.Text) Then
            MessageBox.Show("Vui lòng chọn một danh sách tệp hợp lệ.")
            Return
        End If

        Dim projectName As String = CurrentProjectName()
        Dim statePath As String = GetStatePath(projectName)

        If DownloadQueueState.Exists(statePath) Then
            Dim choice As DialogResult = MessageBox.Show(
                "Dự án """ & projectName & """ đang có một phiên tải dở." & vbNewLine &
                "Chọn Yes để XOÁ và tải lại từ đầu, chọn No để dùng nút ""Tiếp tục"" thay vào đó.",
                "Đã có phiên tải dở", MessageBoxButtons.YesNo, MessageBoxIcon.Question)

            If choice = DialogResult.No Then Return
            DownloadQueueState.Delete(statePath)
        End If

        Dim lines As String()
        Try
            lines = File.ReadAllLines(cboFileList.Text)
        Catch ex As Exception
            MessageBox.Show("Không đọc được danh sách: " & ex.Message)
            Return
        End Try

        Dim downloadRoot As String = If(String.IsNullOrWhiteSpace(txtDownloadRoot.Text),
                                         Path.Combine(Directory.GetCurrentDirectory(), "Download"),
                                         txtDownloadRoot.Text)
        Dim projectFolder As String = Path.Combine(downloadRoot, projectName)

        Dim items As New List(Of DownloadItem)
        For Each line As String In lines
            If String.IsNullOrWhiteSpace(line) Then Continue For
            Try
                Dim data As New FileDownloadData(line.Trim())
                Dim it As New DownloadItem
                it.Data = data
                it.LocalPath = data.GetLocalPath(projectFolder)
                items.Add(it)
            Catch ex As Exception
                ' Bỏ qua dòng không phải URL hợp lệ
            End Try
        Next

        If items.Count = 0 Then
            MessageBox.Show("Danh sách không có URL hợp lệ nào.")
            Return
        End If

        ResetProgressUi(items.Count, 0)
        SetRunningButtonsState(True)

        _downloadManager = New DownloadManager()
        AddHandler _downloadManager.FileProgressChanged, AddressOf OnFileProgressChanged
        AddHandler _downloadManager.FileCompleted, AddressOf OnFileCompleted
        AddHandler _downloadManager.QueuePaused, AddressOf OnQueuePaused
        AddHandler _downloadManager.AllCompleted, AddressOf OnAllCompleted

        _downloadManager.Start(items, statePath)
    End Sub

    Private Sub btnResumeDownload_Click(sender As Object, e As EventArgs) Handles btnResumeDownload.Click
        Dim projectName As String = CurrentProjectName()
        Dim statePath As String = GetStatePath(projectName)

        If Not DownloadQueueState.Exists(statePath) Then
            MessageBox.Show("Không có phiên tải dở nào cho dự án """ & projectName & """.")
            Return
        End If

        Dim items As List(Of DownloadItem) = DownloadQueueState.Load(statePath)
        If items.Count = 0 Then
            MessageBox.Show("Tệp trạng thái rỗng hoặc lỗi, không thể tiếp tục.")
            DownloadQueueState.Delete(statePath)
            UpdateResumeHint()
            Return
        End If

        Dim doneCount As Integer = 0
        For Each it As DownloadItem In items
            If it.Status = DownloadStatus.Completed Then doneCount += 1
        Next

        ResetProgressUi(items.Count, doneCount)
        SetRunningButtonsState(True)

        _downloadManager = New DownloadManager()
        AddHandler _downloadManager.FileProgressChanged, AddressOf OnFileProgressChanged
        AddHandler _downloadManager.FileCompleted, AddressOf OnFileCompleted
        AddHandler _downloadManager.QueuePaused, AddressOf OnQueuePaused
        AddHandler _downloadManager.AllCompleted, AddressOf OnAllCompleted

        _downloadManager.ContinueQueue(items, statePath)
    End Sub

    Private Sub btnPauseDownload_Click(sender As Object, e As EventArgs) Handles btnPauseDownload.Click
        If _downloadManager IsNot Nothing Then
            btnPauseDownload.Enabled = False
            lblSummary.Text = "Đang tạm dừng, chờ tệp hiện tại dừng đúng chỗ..."
            _downloadManager.Pause()
        End If
    End Sub

    Private Sub btnCancelDownload_Click(sender As Object, e As EventArgs) Handles btnCancelDownload.Click
        If _downloadManager IsNot Nothing AndAlso _downloadManager.IsBusy Then
            ' Đang tải hoặc vừa mới yêu cầu tạm dừng nhưng luồng còn sống -> huỷ hẳn,
            ' DownloadManager sẽ tự xoá tệp trạng thái và bắn AllCompleted(wasCancelled:=True).
            _downloadManager.CancelAll()
        Else
            ' Không có luồng đang chạy (vd: đã tạm dừng xong) -> tự xoá tệp trạng thái tại đây.
            Dim statePath As String = GetStatePath(CurrentProjectName())
            DownloadQueueState.Delete(statePath)
            ResetProgressUi(0, 0)
            SetRunningButtonsState(False)
            lblSummary.Text = "Đã huỷ phiên tải."
            UpdateResumeHint()
        End If
    End Sub

    Private Sub ResetProgressUi(totalItems As Integer, alreadyDone As Integer)
        _totalItems = totalItems
        progressCurrentFile.Value = 0
        progressDone.Maximum = Math.Max(totalItems, 1)
        progressDone.Value = Math.Min(alreadyDone, progressDone.Maximum)
        progressFailed.Maximum = Math.Max(totalItems, 1)
        progressFailed.Value = 0
        lblCurrentFileInfo.Text = "0 MB / 0 MB"
        lblDonePercent.Text = PercentOf(progressDone.Value, Math.Max(totalItems, 1)) & " %"
        lblFailedPercent.Text = "0 %"
        lblSummary.Text = If(totalItems > 0, "Đang tải " & alreadyDone & "/" & totalItems, "")
    End Sub

    ''' <summary>Bật/tắt các nút theo trạng thái đang chạy hay không.</summary>
    Private Sub SetRunningButtonsState(isRunning As Boolean)
        btnStartDownload.Enabled = Not isRunning
        btnResumeDownload.Enabled = Not isRunning AndAlso DownloadQueueState.Exists(GetStatePath(CurrentProjectName()))
        btnPauseDownload.Enabled = isRunning
        btnCancelDownload.Enabled = isRunning
    End Sub

    Private Sub OnFileProgressChanged(item As DownloadItem, downloadedBytes As Long, totalBytes As Long)
        If InvokeRequired Then
            BeginInvoke(New Action(Of DownloadItem, Long, Long)(AddressOf OnFileProgressChanged), item, downloadedBytes, totalBytes)
            Return
        End If

        If totalBytes > 0 Then
            Dim pct As Integer = CInt(Math.Truncate((downloadedBytes / CDbl(totalBytes)) * 100.0R))
            progressCurrentFile.Value = Math.Max(0, Math.Min(pct, 100))
            lblCurrentFileInfo.Text = String.Format("{0}  -  {1:0.00} MB / {2:0.00} MB",
                item.Data.FileName,
                downloadedBytes / 1024.0R / 1024.0R,
                totalBytes / 1024.0R / 1024.0R)
        Else
            ' Không rõ tổng dung lượng (server không trả Content-Length) - chỉ hiện số đã tải.
            lblCurrentFileInfo.Text = String.Format("{0}  -  {1:0.00} MB", item.Data.FileName, downloadedBytes / 1024.0R / 1024.0R)
        End If
    End Sub

    Private Sub OnFileCompleted(item As DownloadItem, ex As Exception)
        If InvokeRequired Then
            BeginInvoke(New Action(Of DownloadItem, Exception)(AddressOf OnFileCompleted), item, ex)
            Return
        End If

        If item.Status = DownloadStatus.Completed Then
            progressDone.Value = Math.Min(progressDone.Value + 1, progressDone.Maximum)
            lblDonePercent.Text = PercentOf(progressDone.Value, _totalItems) & " %"
        Else
            progressFailed.Value = Math.Min(progressFailed.Value + 1, progressFailed.Maximum)
            lblFailedPercent.Text = PercentOf(progressFailed.Value, _totalItems) & " %"
        End If

        lblSummary.Text = "Đang tải " & (progressDone.Value + progressFailed.Value) & "/" & _totalItems
    End Sub

    Private Sub OnQueuePaused(remainingCount As Integer)
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf OnQueuePaused), remainingCount)
            Return
        End If

        SetRunningButtonsState(False)
        btnResumeDownload.Enabled = True
        lblSummary.Text = "Đã tạm dừng. Còn " & remainingCount & " tệp chưa tải xong - bấm ""Tiếp tục"" khi sẵn sàng."
        UpdateResumeHint()
    End Sub

    Private Sub OnAllCompleted(totalOk As Integer, totalFail As Integer, wasCancelled As Boolean)
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer, Integer, Boolean)(AddressOf OnAllCompleted), totalOk, totalFail, wasCancelled)
            Return
        End If

        SetRunningButtonsState(False)

        If wasCancelled Then
            lblSummary.Text = "Đã huỷ. " & totalOk & " tệp đã tải xong trước đó, " & totalFail & " lỗi/dang dở."
        Else
            lblSummary.Text = "Hoàn tất: " & totalOk & " thành công, " & totalFail & " lỗi (tổng " & _totalItems & ")"
            MessageBox.Show("Đã tải xong." & vbNewLine & totalOk & " thành công, " & totalFail & " lỗi.")
        End If

        UpdateResumeHint()
    End Sub

    Private Shared Function PercentOf(value As Integer, total As Integer) As Integer
        If total <= 0 Then Return 0
        Return CInt(Math.Truncate((value / CDbl(total)) * 100.0R))
    End Function

    ' ========================================================================
    '  DỰNG GIAO DIỆN BẰNG CODE (thay cho Form1.Designer.vb)
    ' ========================================================================

    Private Sub InitializeComponent()
        Me.Text = "Trình tạo danh sách & tải tệp - 2CongLC"
        Me.ClientSize = New Size(620, 510)
        Me.FormBorderStyle = FormBorderStyle.FixedSingle
        Me.MaximizeBox = False
        Me.StartPosition = FormStartPosition.CenterScreen
        Me.Font = New Font("Segoe UI", 9.0F)

        folderDialog = New FolderBrowserDialog()

        ' ---- GroupBox 1: Tạo danh sách ----
        Dim grp1 As New GroupBox With {.Text = "Tạo danh sách liên kết tải", .Location = New Point(12, 12), .Size = New Size(596, 150)}

        Dim lblSrc As New Label With {.Text = "Thư mục nguồn:", .Location = New Point(10, 28), .AutoSize = True}
        txtSourceFolder = New TextBox With {.Location = New Point(130, 25), .Width = 360}
        btnBrowseSource = New Button With {.Text = "Chọn...", .Location = New Point(500, 23), .Width = 80}

        Dim lblPattern As New Label With {.Text = "Mẫu tệp:", .Location = New Point(10, 58), .AutoSize = True}
        txtPattern = New TextBox With {.Location = New Point(130, 55), .Width = 100, .Text = "*.*"}

        Dim lblBaseUrl As New Label With {.Text = "URL gốc (tuỳ chọn):", .Location = New Point(10, 88), .AutoSize = True}
        txtBaseUrl = New TextBox With {.Location = New Point(130, 85), .Width = 450}

        Dim lblListName As New Label With {.Text = "Tên tệp danh sách:", .Location = New Point(10, 118), .AutoSize = True}
        txtListName = New TextBox With {.Location = New Point(130, 115), .Width = 260}
        btnGenerateList = New Button With {.Text = "Tạo danh sách", .Location = New Point(430, 113), .Width = 150}

        grp1.Controls.AddRange(New Control() {lblSrc, txtSourceFolder, btnBrowseSource, lblPattern, txtPattern,
                                 lblBaseUrl, txtBaseUrl, lblListName, txtListName, btnGenerateList})

        ' ---- GroupBox 2: Tải tệp ----
        Dim grp2 As New GroupBox With {.Text = "Tải tệp theo danh sách", .Location = New Point(12, 172), .Size = New Size(596, 326)}

        Dim lblList As New Label With {.Text = "Danh sách:", .Location = New Point(10, 28), .AutoSize = True}
        cboFileList = New ComboBox With {.Location = New Point(130, 25), .Width = 300, .DropDownStyle = ComboBoxStyle.DropDown}
        btnRefreshList = New Button With {.Text = "Làm mới", .Location = New Point(438, 23), .Width = 70}
        btnOpenLocation = New Button With {.Text = "Mở vị trí", .Location = New Point(512, 23), .Width = 70}

        Dim lblProject As New Label With {.Text = "Tên dự án:", .Location = New Point(10, 58), .AutoSize = True}
        txtProjectName = New TextBox With {.Location = New Point(130, 55), .Width = 200, .Text = "project1"}

        Dim lblDownloadRoot As New Label With {.Text = "Thư mục tải về:", .Location = New Point(10, 88), .AutoSize = True}
        txtDownloadRoot = New TextBox With {.Location = New Point(130, 85), .Width = 360}
        btnBrowseDownloadRoot = New Button With {.Text = "Chọn...", .Location = New Point(500, 83), .Width = 80}

        btnStartDownload = New Button With {.Text = "Bắt đầu tải", .Location = New Point(130, 120), .Width = 108}
        btnPauseDownload = New Button With {.Text = "Tạm dừng", .Location = New Point(244, 120), .Width = 90, .Enabled = False}
        btnResumeDownload = New Button With {.Text = "Tiếp tục", .Location = New Point(340, 120), .Width = 90, .Enabled = False}
        btnCancelDownload = New Button With {.Text = "Huỷ", .Location = New Point(436, 120), .Width = 80, .Enabled = False}

        lblResumeHint = New Label With {.Text = "", .Location = New Point(10, 150), .AutoSize = False, .Width = 576, .Height = 16, .ForeColor = Color.DarkOrange}

        Dim lblCur As New Label With {.Text = "Tệp hiện tại:", .Location = New Point(10, 190), .AutoSize = True}
        progressCurrentFile = New ProgressBar With {.Location = New Point(130, 187), .Width = 452, .Height = 18}
        lblCurrentFileInfo = New Label With {.Text = "0 MB / 0 MB", .Location = New Point(130, 208), .AutoSize = True}

        Dim lblDone As New Label With {.Text = "Đã tải xong:", .Location = New Point(10, 235), .AutoSize = True}
        progressDone = New ProgressBar With {.Location = New Point(130, 232), .Width = 380, .Height = 18, .ForeColor = Color.Green}
        lblDonePercent = New Label With {.Text = "0 %", .Location = New Point(518, 235), .AutoSize = True}

        Dim lblFail As New Label With {.Text = "Lỗi:", .Location = New Point(10, 262), .AutoSize = True}
        progressFailed = New ProgressBar With {.Location = New Point(130, 259), .Width = 380, .Height = 18}
        lblFailedPercent = New Label With {.Text = "0 %", .Location = New Point(518, 262), .AutoSize = True}

        lblSummary = New Label With {.Text = "", .Location = New Point(10, 292), .AutoSize = False, .Width = 576, .Height = 20}

        grp2.Controls.AddRange(New Control() {lblList, cboFileList, btnRefreshList, btnOpenLocation,
                                 lblProject, txtProjectName, lblDownloadRoot, txtDownloadRoot, btnBrowseDownloadRoot,
                                 btnStartDownload, btnPauseDownload, btnResumeDownload, btnCancelDownload, lblResumeHint,
                                 lblCur, progressCurrentFile, lblCurrentFileInfo,
                                 lblDone, progressDone, lblDonePercent,
                                 lblFail, progressFailed, lblFailedPercent,
                                 lblSummary})

        Me.Controls.Add(grp1)
        Me.Controls.Add(grp2)
    End Sub

End Class
