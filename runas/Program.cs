
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

void WriteLog(string message)
{
    try
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "runas.log");
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }
    catch
    {

    }
    Console.WriteLine(message);
}

void ExitWithError(string message, int exitCode = 1)
{
    WriteLog($"ERROR: {message}");
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    Environment.Exit(exitCode);
}

WriteLog($"runas.exe started with {args.Length} argument(s)");
if (args.Length > 0)
    WriteLog($"Arguments: {string.Join(" | ", args.Select((a, i) => $"[{i}]={a}"))}");

if (args.Length < 2)
{
    Console.WriteLine("Please do not launch this process directly.");
}

var argString = "";
if (args.Length > 2)
{
    for (var i = 2; i < args.Length; i++)
    {
        argString += args[i] + " ";
    }
}

try
{
    var filePath = args[0].Trim().Trim('"');
    WriteLog($"Processing file path: '{filePath}'");

    if (string.IsNullOrWhiteSpace(filePath))
        ExitWithError("File path is empty or whitespace");

    string targetDir;

    if (Directory.Exists(filePath))
    {
        targetDir = filePath;
        WriteLog($"Path is a directory: {targetDir}");
    }
    else if (filePath.EndsWith("\\") || filePath.EndsWith("/"))
    {
        targetDir = filePath.TrimEnd('\\', '/');
        WriteLog($"Path ends with separator, trimmed to: {targetDir}");

        if (!Directory.Exists(targetDir))
        {
            var parentDir = Path.GetDirectoryName(targetDir) ?? string.Empty;
            WriteLog($"Directory doesn't exist, trying parent: {parentDir}");
            targetDir = parentDir;
        }
    }
    else if (File.Exists(filePath))
    {
        targetDir = Path.GetDirectoryName(filePath) ?? string.Empty;
        WriteLog($"Path is a file, directory: {targetDir}");
    }
    else
    {
        targetDir = Path.GetDirectoryName(filePath) ?? string.Empty;
        WriteLog($"Path doesn't exist as file or directory, trying directory name: {targetDir}");
    }

    if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
    {
        WriteLog($"Target directory invalid or doesn't exist: '{targetDir}', trying fallbacks...");
        var exeDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
        {
            targetDir = exeDir;
            WriteLog($"Using executable directory: {targetDir}");
        }
        else
        {
            targetDir = Directory.GetCurrentDirectory();
            WriteLog($"Using current directory: {targetDir}");
        }
    }

    if (string.IsNullOrWhiteSpace(targetDir))
    {
        targetDir = Environment.CurrentDirectory;
        WriteLog($"Using Environment.CurrentDirectory: {targetDir}");
    }

    WriteLog($"Setting current directory to: {targetDir}");
    Directory.SetCurrentDirectory(targetDir);
    WriteLog($"Current directory set successfully");

    var isElevated = args[1] == "1";
    WriteLog($"Starting process: FileName='{filePath}', Elevated={isElevated}, Arguments='{argString.Trim()}', WorkingDirectory='{targetDir}'");

    var processInfo = new ProcessStartInfo
    {
        FileName = filePath,
        UseShellExecute = true,
        RedirectStandardError = false,
        RedirectStandardOutput = false,
        Arguments = argString.Trim(),
        Verb = isElevated ? "runas" : "",
        WorkingDirectory = targetDir
    };

    var process = Process.Start(processInfo);
    if (process != null)
    {
        WriteLog($"Process started successfully (PID: {process.Id})");
    }
    else
    {
        WriteLog("WARNING: Process.Start returned null");
    }
}
catch (Win32Exception e)
{
    if (e.HResult != -2147467259)
    {
        ExitWithError($"Win32Exception: {e.Message} (HResult: {e.HResult})");
    }
    else
    {
        WriteLog("User cancelled elevation prompt");
    }
}
catch (Exception e)
{
    ExitWithError($"Exception: {e.GetType().Name} - {e.Message}{Environment.NewLine}Stack Trace: {e.StackTrace}");
}
