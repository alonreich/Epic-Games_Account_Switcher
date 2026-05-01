using System;
using System.Diagnostics;
using System.Linq;

namespace runas
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = string.Join(" ", args.Skip(1)),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
        }
    }
}
