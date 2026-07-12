Imports System
Imports System.Collections.Generic
Imports System.IO

''' <summary>
''' Chịu trách nhiệm quét một thư mục nguồn theo mẫu tệp (vd *.png, *.*),
''' sinh ra danh sách URL tương ứng, và lưu danh sách đó ra tệp .txt.
''' Tách hẳn khỏi Form1 để có thể tái sử dụng / kiểm thử độc lập.
''' </summary>
Public Class FileListBuilder

    ''' <summary>
    ''' Quét sourceFolder (đệ quy) theo pattern, trả về danh sách URL.
    ''' Nếu baseUrl rỗng, trả về đường dẫn tuyệt đối trên máy thay vì URL.
    ''' </summary>
    Public Shared Function BuildUrlList(sourceFolder As String, pattern As String, baseUrl As String) As List(Of String)
        Dim result As New List(Of String)

        If Not Directory.Exists(sourceFolder) Then Return result

        Dim normalizedBase As String = Nothing
        If Not String.IsNullOrWhiteSpace(baseUrl) Then
            normalizedBase = If(baseUrl.EndsWith("/"), baseUrl, baseUrl & "/")
        End If

        Dim root As String = sourceFolder.TrimEnd(Path.DirectorySeparatorChar)
        Dim files As String() = Directory.GetFiles(root, pattern, SearchOption.AllDirectories)

        For Each filePath As String In files
            If normalizedBase IsNot Nothing Then
                Dim relative As String = filePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
                result.Add(normalizedBase & relative.Replace(Path.DirectorySeparatorChar, "/"c))
            Else
                result.Add(filePath)
            End If
        Next

        Return result
    End Function

    ''' <summary>
    ''' Lưu danh sách ra thư mục "data" bên cạnh chương trình (tạo nếu chưa có).
    ''' Trả về đường dẫn đầy đủ của tệp đã lưu.
    ''' </summary>
    Public Shared Function SaveList(lines As List(Of String), outputFolder As String, Optional fileName As String = Nothing) As String
        If Not Directory.Exists(outputFolder) Then Directory.CreateDirectory(outputFolder)

        Dim finalName As String = fileName
        If String.IsNullOrWhiteSpace(finalName) Then
            finalName = "flist_" & DateTime.Now.ToString("MM-dd-yyyy_HH-mm-ss") & ".txt"
        ElseIf Not finalName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) Then
            finalName &= ".txt"
        End If

        Dim fullPath As String = Path.Combine(outputFolder, finalName)
        File.WriteAllLines(fullPath, lines)
        Return fullPath
    End Function

End Class
