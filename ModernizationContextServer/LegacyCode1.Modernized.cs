using System;
using System.Threading;
using System.Threading.Tasks;

namespace LegacyApp
{
    class Worker
    {
        public async Task ProcessData()
{
    Console.WriteLine("Starting...");
    // Modernized: Non-blocking delay
    await Task.Delay(5000);
    Console.WriteLine("Done!");
}

        public void SafeMethod()
        {
            Console.WriteLine("Nothing to see here.");
        }
    }

    class Program
    {
        static async Task Main()
        {
            var worker = new Worker();
            await worker.ProcessData();
            worker.SafeMethod();
        }
    }
}
