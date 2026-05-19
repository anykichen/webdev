using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace DragUploadToNas
{
    class Program
    {
        private static readonly string NasWebDavUrl = "http://10.201.2.31:5005/上传的文件/";
        private static readonly string UserName     = "user";
        private static readonly string Pwd          = "Cc880821/";

        // 上传选项
        private static readonly int    RetryCount      = 3;
        private static readonly int    RetryDelayMs    = 1500;
        private static readonly bool   SkipIfExists    = true;   // HEAD 检查重复
        private static readonly long   MaxFileSizeBytes = 4L * 1024 * 1024 * 1024; // 4 GB

        // 日志
        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "upload_log.txt");

        private static int _successCount;
        private static int _skipCount;
        private static int _failCount;
        private static readonly List<string> _failedFiles = new List<string>();
        private static readonly object _consoleLock = new object();

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }

            Log($"===== 上传开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

            // 展开所有文件（包含文件夹内的文件）
            var allFiles = ExpandPaths(args).ToList();
            if (allFiles.Count == 0)
            {
                WriteConsole("  没有找到可上传的文件。", ConsoleColor.Yellow);
                return;
            }

            WriteConsole($"  共找到 {allFiles.Count} 个文件，开始上传...\n", ConsoleColor.Cyan);

            int index = 0;
            foreach (var file in allFiles)
            {
                index++;
                UploadSingleFile(file, index, allFiles.Count);
            }

            PrintSummary();
            Log($"===== 上传结束  成功:{_successCount} 跳过:{_skipCount} 失败:{_failCount} =====\n");

            if (_failCount > 0)
            {
                WriteConsole("\n  按任意键退出...", ConsoleColor.White);
                Console.ReadKey(true);
            }
            else
            {
                Thread.Sleep(1800); // 成功时短暂停留让用户看到结果
            }
        }

        // ─── 文件展开（支持文件夹递归）──────────────────────────────────────
        static IEnumerable<string> ExpandPaths(string[] paths)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    yield return path;
                }
                else if (Directory.Exists(path))
                {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        yield return f;
                }
            }
        }

        // ─── 单文件上传 ──────────────────────────────────────────────────────
        static void UploadSingleFile(string localPath, int index, int total)
        {
            string fileName = Path.GetFileName(localPath);
            string prefix   = $"[{index}/{total}]";

            // 大小检查
            var fileInfo = new FileInfo(localPath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                WriteConsole($"{prefix} 跳过(超大): {fileName}", ConsoleColor.Yellow);
                Log($"SKIP  超过大小限制: {localPath}");
                Interlocked.Increment(ref _skipCount);
                return;
            }

            // 拼接远端 URL（保留子目录相对结构可选，此处仅用文件名）
            string remoteUrl = NasWebDavUrl + Uri.EscapeDataString(fileName);

            // 重复检查
            if (SkipIfExists && RemoteFileExists(remoteUrl))
            {
                WriteConsole($"{prefix} 跳过(已存在): {fileName}", ConsoleColor.DarkYellow);
                Log($"SKIP  已存在: {localPath}");
                Interlocked.Increment(ref _skipCount);
                return;
            }

            WriteConsole($"{prefix} 上传中: {fileName}", ConsoleColor.White);

            bool ok = false;
            string errMsg = "";

            for (int attempt = 1; attempt <= RetryCount; attempt++)
            {
                try
                {
                    ok = UploadWithProgress(localPath, remoteUrl, fileInfo.Length, prefix);
                    if (ok) break;
                }
                catch (Exception ex)
                {
                    errMsg = ex.Message;
                    if (attempt < RetryCount)
                    {
                        WriteConsole($"  ↺ 第{attempt}次失败，{RetryDelayMs}ms 后重试: {ex.Message}",
                            ConsoleColor.DarkRed);
                        Thread.Sleep(RetryDelayMs);
                    }
                }
            }

            if (ok)
            {
                WriteConsole($"{prefix} ✓ 完成: {fileName} ({FormatSize(fileInfo.Length)})",
                    ConsoleColor.Green);
                Log($"OK    {localPath}");
                Interlocked.Increment(ref _successCount);
            }
            else
            {
                WriteConsole($"{prefix} ✗ 失败: {fileName}  [{errMsg}]", ConsoleColor.Red);
                Log($"FAIL  {localPath}  [{errMsg}]");
                Interlocked.Increment(ref _failCount);
                lock (_failedFiles) _failedFiles.Add(localPath);
            }
        }

        // ─── 带进度回调的上传 ────────────────────────────────────────────────
        static bool UploadWithProgress(string localPath, string remoteUrl, long fileSize, string prefix)
        {
            const int BufSize = 256 * 1024; // 256 KB
            byte[] buf = new byte[BufSize];
            long sent = 0;
            int lastPct = -1;

            var req = (HttpWebRequest)WebRequest.Create(remoteUrl);
            req.Method      = "PUT";
            req.Credentials = new NetworkCredential(UserName, Pwd);
            req.ContentLength = fileSize;
            req.Timeout     = 120_000;  // 120 s 建连
            req.ReadWriteTimeout = 300_000; // 5 min 传输

            using (var fs   = new FileStream(localPath, FileMode.Open, FileAccess.Read))
            using (var rs   = req.GetRequestStream())
            {
                int read;
                while ((read = fs.Read(buf, 0, BufSize)) > 0)
                {
                    rs.Write(buf, 0, read);
                    sent += read;

                    int pct = fileSize > 0 ? (int)(sent * 100 / fileSize) : 100;
                    if (pct != lastPct && pct % 10 == 0)
                    {
                        lastPct = pct;
                        lock (_consoleLock)
                        {
                            Console.CursorLeft = 0;
                            Console.Write($"  {prefix} [{new string('█', pct / 5),-20}] {pct,3}%  {FormatSize(sent)}/{FormatSize(fileSize)}   ");
                        }
                    }
                }
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                lock (_consoleLock) { Console.CursorLeft = 0; Console.Write(new string(' ', 60) + "\r"); }
                return resp.StatusCode == HttpStatusCode.Created
                    || resp.StatusCode == HttpStatusCode.NoContent
                    || resp.StatusCode == HttpStatusCode.OK;
            }
        }

        // ─── HEAD 检查远端文件是否存在 ───────────────────────────────────────
        static bool RemoteFileExists(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method      = "HEAD";
                req.Credentials = new NetworkCredential(UserName, Pwd);
                req.Timeout     = 8_000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch (WebException ex) when
                (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
            catch { return false; }
        }

        // ─── 汇总 ────────────────────────────────────────────────────────────
        static void PrintSummary()
        {
            Console.WriteLine();
            WriteConsole("──────────────── 上传汇总 ────────────────", ConsoleColor.Cyan);
            WriteConsole($"  ✓ 成功: {_successCount}", ConsoleColor.Green);
            WriteConsole($"  ⊘ 跳过: {_skipCount}",   ConsoleColor.DarkYellow);
            WriteConsole($"  ✗ 失败: {_failCount}",   ConsoleColor.Red);

            if (_failedFiles.Count > 0)
            {
                WriteConsole("\n  失败文件列表:", ConsoleColor.Red);
                foreach (var f in _failedFiles)
                    WriteConsole($"    • {f}", ConsoleColor.DarkRed);
            }
            WriteConsole("──────────────────────────────────────────", ConsoleColor.Cyan);
        }

        // ─── 工具方法 ────────────────────────────────────────────────────────
        static void WriteConsole(string msg, ConsoleColor color)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(msg);
                Console.ResetColor();
            }
        }

        static void Log(string msg)
        {
            try
            {
                lock (_consoleLock)
                    File.AppendAllText(LogPath, msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* 日志失败不影响上传 */ }
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024)        return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }

        static void ShowUsage()
        {
            WriteConsole("DragUploadToNas — 拖拽文件到此程序即可上传到 NAS", ConsoleColor.Cyan);
            WriteConsole("  用法: 将文件或文件夹直接拖拽到 .exe 图标上", ConsoleColor.White);
            WriteConsole($"  目标: {NasWebDavUrl}", ConsoleColor.DarkGray);
            WriteConsole($"  日志: {LogPath}",       ConsoleColor.DarkGray);
            Thread.Sleep(3000);
        }
    }
}
