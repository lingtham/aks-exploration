using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Reducer
{
    class Logger
    {
        private string path;
        public Logger(string dir)
        {
            path = "C:\\" + dir + "\\Logs";
            Directory.CreateDirectory(path);
        }
 
        public void countLog(StringBuilder message)
        {
            using (StreamWriter w = File.AppendText(path + "\\countlogs.txt"))
            {
                w.WriteLine($"{DateTime.Now} - {message}");
                w.WriteLine("-------------");
            }
        }

        public void errorLog(String error)
        {
            using (StreamWriter w = File.AppendText(path + "\\errorlogs.txt"))
            {
                w.WriteLine($"{DateTime.Now} - {error}");
                w.WriteLine("-------------");
            }
        }

        public void resultsLog(StringBuilder message)
        {
            using (StreamWriter w = File.AppendText(path + "\\sendResultsLogs.txt"))
            {
                w.WriteLine($"{DateTime.Now} - {message}");
                w.WriteLine("-------------");
            }
        }

        public void leaseLog(string message)
        {
            using (StreamWriter w = File.AppendText(path + "\\leaseLogs.txt"))
            {
                w.WriteLine($"{DateTime.Now} - {message}");
                w.WriteLine("-------------");
            }
        }
    }
}
