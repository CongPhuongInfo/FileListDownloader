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
''' theo danh sách đó về máy, có tiến độ theo từng tệp và tổng thể.
''' Viết lại theo hướng tách lớp: FileListBuilder lo việc quét/sinh danh sách,
''' DownloadManager lo việc tải tuần tự, Form1 chỉ còn nhiệm vụ hiển thị.
''' Giao diện dựng bằng code (không dùng Designer.vb) để build được bằng vbc.exe.
''' </summary>
Public Class Form1
    Inherits Form

    ' ==== Khối 1: Tạo danh sách URL ====
    ' Ghi chú: các control có gắn "Handles X.Click" bắt buộc phải khai báo WithEvents,
    ' nếu không vbc sẽ báo lỗi "Handles clause requires a WithEvents variable".
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
    Private txtProjectName As TextBox
    Private txtDownloadRoot As TextBox
    Private WithEvents btnBrowseDownloadRoot As Button
    Private WithEvents btnStartDownload As Button
    Private WithEvents btnCancelDownload As Button

    Private progressCurrentFile As ProgressBar
    Private lblCurrentFileInfo As Label
    Private progressDone As ProgressBar
    Private lblDonePercent As Label
    Private progressFailed As ProgressBar
    Private lblFailedPercent As Label
    Private lblSummary As Label

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
    '  KHỐI 2 - TẢI TỆP THEO DANH SÁCH
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

    Private Sub btnStartDownload_Click(sender As Object, e As EventArgs) Handles btnStartDownload.Click
        If String.IsNullOrWhiteSpace(cboFileList.Text) OrElse Not File.Exists(cboFileList.Text) Then
            MessageBox.Show("Vui lòng chọn một danh sách tệp hợp lệ.")
            Return
        End If

        Dim lines As String()
        Try
            lines = File.ReadAllLines(cboFileList.Text)
        Catch ex As Exception
            MessageBox.Show("Không đọc được danh sách: " & ex.Message)
            Return
        End Try

        Dim projectName As String = If(String.IsNullOrWhiteSpace(txtProjectName.Text), "project1", txtProjectName.Text)
        Dim downloadRoot As String = If(String.IsNullOrWhiteSpace(txtDownloadRoot.Text),
                                         Path.Combine(Directory.GetCurrentDirectory(), "Download"),
                                         txtDownloadRoot.Text)
        Dim projectFolder As String = Path.Combine(downloadRoot, projectName)

        Dim items As New List(Of DownloadItem)
        For Each line As String In lines
            If String.IsNullOrWhiteSpace(line) Then Continue For
            Try
                Dim data As New FileDownloadData(line.Trim())
                items.Add(New DownloadItem With {
                    .Data = data,
                    .LocalPath = data.GetLocalPath(projectFolder)
                })
            Catch ex As Exception
                ' Bỏ qua dòng không phải URL hợp lệ
            End Try
        Next

        If items.Count = 0 Then
            MessageBox.Show("Danh sách không có URL hợp lệ nào.")
            Return
        End If

        _totalItems = items.Count
        progressCurrentFile.Value = 0
        progressDone.Value = 0
        progressDone.Maximum = _totalItems
        progressFailed.Value = 0
        progressFailed.Maximum = _totalItems
        lblCurrentFileInfo.Text = "0 MB / 0 MB"
        lblDonePercent.Text = "0 %"
        lblFailedPercent.Text = "0 %"
        lblSummary.Text = "Đang tải 0/" & _totalItems

        btnStartDownload.Enabled = False
        btnCancelDownload.Enabled = True

        _downloadManager = New DownloadManager()
        AddHandler _downloadManager.FileProgressChanged, AddressOf OnFileProgressChanged
        AddHandler _downloadManager.FileCompleted, AddressOf OnFileCompleted
        AddHandler _downloadManager.AllCompleted, AddressOf OnAllCompleted

        _downloadManager.Start(items)
    End Sub

    Private Sub btnCancelDownload_Click(sender As Object, e As EventArgs) Handles btnCancelDownload.Click
        If _downloadManager IsNot Nothing Then
            _downloadManager.CancelAll()
        End If
    End Sub

    Private Sub OnFileProgressChanged(item As DownloadItem, e As DownloadProgressChangedEventArgs)
        If InvokeRequired Then
            BeginInvoke(New Action(Of DownloadItem, DownloadProgressChangedEventArgs)(AddressOf OnFileProgressChanged), item, e)
            Return
        End If

        progressCurrentFile.Value = Math.Min(e.ProgressPercentage, 100)
        lblCurrentFileInfo.Text = String.Format("{0}  -  {1:0.00} MB / {2:0.00} MB",
            item.Data.FileName,
            e.BytesReceived / 1024.0R / 1024.0R,
            e.TotalBytesToReceive / 1024.0R / 1024.0R)
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

    Private Sub OnAllCompleted(totalOk As Integer, totalFail As Integer)
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer, Integer)(AddressOf OnAllCompleted), totalOk, totalFail)
            Return
        End If

        btnStartDownload.Enabled = True
        btnCancelDownload.Enabled = False
        lblSummary.Text = "Hoàn tất: " & totalOk & " thành công, " & totalFail & " lỗi (tổng " & _totalItems & ")"
        MessageBox.Show("Đã tải xong." & vbNewLine & totalOk & " thành công, " & totalFail & " lỗi.")
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
        Me.ClientSize = New Size(620, 480)
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
        Dim grp2 As New GroupBox With {.Text = "Tải tệp theo danh sách", .Location = New Point(12, 172), .Size = New Size(596, 296)}

        Dim lblList As New Label With {.Text = "Danh sách:", .Location = New Point(10, 28), .AutoSize = True}
        cboFileList = New ComboBox With {.Location = New Point(130, 25), .Width = 300, .DropDownStyle = ComboBoxStyle.DropDown}
        btnRefreshList = New Button With {.Text = "Làm mới", .Location = New Point(438, 23), .Width = 70}
        btnOpenLocation = New Button With {.Text = "Mở vị trí", .Location = New Point(512, 23), .Width = 70}

        Dim lblProject As New Label With {.Text = "Tên dự án:", .Location = New Point(10, 58), .AutoSize = True}
        txtProjectName = New TextBox With {.Location = New Point(130, 55), .Width = 200, .Text = "project1"}

        Dim lblDownloadRoot As New Label With {.Text = "Thư mục tải về:", .Location = New Point(10, 88), .AutoSize = True}
        txtDownloadRoot = New TextBox With {.Location = New Point(130, 85), .Width = 360}
        btnBrowseDownloadRoot = New Button With {.Text = "Chọn...", .Location = New Point(500, 83), .Width = 80}

        btnStartDownload = New Button With {.Text = "Bắt đầu tải", .Location = New Point(130, 120), .Width = 120}
        btnCancelDownload = New Button With {.Text = "Huỷ", .Location = New Point(260, 120), .Width = 80, .Enabled = False}

        Dim lblCur As New Label With {.Text = "Tệp hiện tại:", .Location = New Point(10, 160), .AutoSize = True}
        progressCurrentFile = New ProgressBar With {.Location = New Point(130, 157), .Width = 452, .Height = 18}
        lblCurrentFileInfo = New Label With {.Text = "0 MB / 0 MB", .Location = New Point(130, 178), .AutoSize = True}

        Dim lblDone As New Label With {.Text = "Đã tải xong:", .Location = New Point(10, 205), .AutoSize = True}
        progressDone = New ProgressBar With {.Location = New Point(130, 202), .Width = 380, .Height = 18, .ForeColor = Color.Green}
        lblDonePercent = New Label With {.Text = "0 %", .Location = New Point(518, 205), .AutoSize = True}

        Dim lblFail As New Label With {.Text = "Lỗi:", .Location = New Point(10, 232), .AutoSize = True}
        progressFailed = New ProgressBar With {.Location = New Point(130, 229), .Width = 380, .Height = 18}
        lblFailedPercent = New Label With {.Text = "0 %", .Location = New Point(518, 232), .AutoSize = True}

        lblSummary = New Label With {.Text = "", .Location = New Point(10, 262), .AutoSize = True, .Width = 570}

        grp2.Controls.AddRange(New Control() {lblList, cboFileList, btnRefreshList, btnOpenLocation,
                                 lblProject, txtProjectName, lblDownloadRoot, txtDownloadRoot, btnBrowseDownloadRoot,
                                 btnStartDownload, btnCancelDownload,
                                 lblCur, progressCurrentFile, lblCurrentFileInfo,
                                 lblDone, progressDone, lblDonePercent,
                                 lblFail, progressFailed, lblFailedPercent,
                                 lblSummary})

        Me.Controls.Add(grp1)
        Me.Controls.Add(grp2)
    End Sub

End Class
