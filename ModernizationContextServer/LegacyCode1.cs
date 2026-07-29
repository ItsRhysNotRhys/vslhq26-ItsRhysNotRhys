using System;
using System.Threading;

namespace LegacyApp
{
    class Worker
    {
        public void ProcessData()
        {
            Console.WriteLine("Starting...");
            // Anti-pattern: Blocking the thread
            Thread.Sleep(5000); 
            Console.WriteLine("Done!");
        }

        public void SafeMethod()
        {
            Console.WriteLine("Nothing to see here.");
        }
    }

    class Program
    {
        static void Main()
        {
            var worker = new Worker();
            worker.ProcessData();
            worker.SafeMethod();
        }
    }
}
