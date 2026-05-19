using System;
using System.IO;
using System.Net;

namespace Nas拖拽直传工具
{
    class Program
    {
        // 已填好你的NAS信息
        private static readonly string WebDavPath = "http://10.201.2.31:5005/上传的文件/";
        private static readonly string LoginUser = "user";
        private static readonly string LoginPwd = "Cc880821/";

        static void Main(string[] args)
        {
            // 无拖拽文件直接退出
            if (args.Length <= 0)
                return;

            foreach (var item in args)
            {
                if (File.Exists(item))
                {
                    QuickUpload(item);
                }
            }
        }

        /// <summary>
        /// 静默上传 无弹窗 无提示
        /// </summary>
        static void QuickUpload(string localFile)
        {
            try
            {
                string fname = Path.GetFileName(localFile);
                string targetUrl = WebDavPath + fname;

                using WebClient client = new WebClient();
                client.Credentials = new NetworkCredential(LoginUser, LoginPwd);
                client.UploadFile(targetUrl, "PUT", localFile);
            }
            catch
            {
                // 静默报错，不给使用者任何提示
            }
        }
    }
}
