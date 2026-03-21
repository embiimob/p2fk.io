using System.Diagnostics;
using System.Reflection;
using System.Text;
using static System.Net.WebRequestMethods;

namespace P2FK.IO
{
    public class Wrapper
    {
        //default mainnet connection info
        public string ProdCLIPath = @"C:\SUP\SUP.exe"; // Replace with the actual path to SUP.EXE
        public string ProdVersionByte = @"0"; // Replace with the actual version byte
        public string ProdRPCURL = @"http://127.0.0.1:8332";
        public string ProdRPCUser = "good-user";
        public string ProdRPCPassword = "better-password";

        //default testnet connection info
        public string TestCLIPath = @"C:\SUP\SUP.exe"; // Replace with the actual path to SUP.EXE
        public string TestVersionByte = @"111"; // Replace with the actual version byte
        public string TestRPCURL = @"http://127.0.0.1:18332";
        public string TestRPCUser = "good-user";
        public string TestRPCPassword = "better-password";

        //default litecoin mainnet connection info
        public string LTCCLIPath = @"C:\SUP\SUP.exe";
        public string LTCVersionByte = @"48";
        public string LTCRPCURL = @"http://127.0.0.1:9332";
        public string LTCRPCUser = "good-user";
        public string LTCRPCPassword = "better-password";

        //default dogecoin mainnet connection info
        public string DOGCLIPath = @"C:\SUP\SUP.exe";
        public string DOGVersionByte = @"30";
        public string DOGRPCURL = @"http://127.0.0.1:22555";
        public string DOGRPCUser = "good-user";
        public string DOGRPCPassword = "better-password";

        //root folder where synced blockchain data (ROOT.json, OBJ.json, etc.) is stored
        public string RootPath = @"C:\p2fk.io\root";

        //default mazacoin mainnet connection info
        public string MZCCLIPath = @"C:\SUP\SUP.exe";
        public string MZCVersionByte = @"50";
        public string MZCRPCURL = @"http://127.0.0.1:12832";
        public string MZCRPCUser = "good-user";
        public string MZCRPCPassword = "better-password";

        public const int MaxTimeoutSeconds = 420;

        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(MaxTimeoutSeconds);

        public async Task<string> RunCommandAsync(string executablePath, string arguments, CancellationToken cancellationToken = default)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8, // Set the standard output encoding to UTF-8
                StandardErrorEncoding = Encoding.UTF8   // Set the standard error encoding to UTF-8
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                await Task.WhenAll(outputTask, errorTask);
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* process may have already exited */ }

                if (timeoutCts.IsCancellationRequested)
                    return "[\"error: request timed out\"]";

                return "[\"error: request cancelled\"]";
            }

            // Task.WhenAll above succeeded without throwing, so both tasks are guaranteed completed successfully
            string output = outputTask.Result;
            string error = errorTask.Result;

            if (!string.IsNullOrEmpty(error))
            {
                return $"Error: {error}";
            }

            return output;
        }
    }
}
